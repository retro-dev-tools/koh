using Koh.Compiler.Backends.Sm83.Mir;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Analysis;

namespace Koh.Compiler.Backends.Sm83;

/// <summary>
/// The WRAM layout of one function's frame: fixed addresses for parameters and SSA values,
/// compile-time addresses for <c>alloca</c>/constant-<c>gep</c> pointers, and scratch for
/// phi-cycle breaking. Computed for the whole module before any code is emitted so a caller
/// knows where to place a callee's arguments.
///
/// A small set of short-lived byte values may instead be held in a CPU register (<see cref="Register"/>)
/// rather than a WRAM slot — the first increment of register residency (SOTA item #2). See
/// <see cref="SelectResidents"/> for the (deliberately conservative) candidate rule.
/// </summary>
internal sealed class FunctionAllocation
{
    private static readonly ReferenceEqualityComparer Eq = ReferenceEqualityComparer.Instance;

    public required Dictionary<IrValue, int> Slot { get; init; }
    public required Dictionary<IrValue, int> StaticAddr { get; init; }

    /// <summary>Values held in a CPU register instead of a WRAM slot. A residency-assigned value has no
    /// entry in <see cref="Slot"/>: the emitter sinks its producing instruction's result into the register
    /// and sources every use from there. Empty unless residency is enabled for this function.</summary>
    public required Dictionary<IrValue, Sm83Register> Register { get; init; }
    public required int PhiTempBase { get; init; }
    public required int FrameBase { get; init; }
    public required int FrameEnd { get; init; }

    /// <param name="allowResidency">Whether short-lived byte values may be assigned CPU registers.
    /// Disabled for interrupt handlers and recursive functions, whose prologues/epilogues and
    /// frame-save paths impose register constraints the conservative residency model does not yet
    /// reason about.</param>
    public static FunctionAllocation For(IrFunction fn, int baseAddr, bool allowResidency = false)
    {
        var staticAddr = new Dictionary<IrValue, int>(Eq);
        var slot = new Dictionary<IrValue, int>(Eq);
        int wram = baseAddr;
        int phiTempBytes = 0;

        // Parameters get fixed slots first: the caller writes them at these addresses, so they
        // are a stable ABI and are never reused by colouring.
        foreach (var p in fn.Parameters)
        {
            slot[p] = wram;
            wram += p.Type.SizeInBytes;
        }

        // Permanent storage: alloca objects and constant-index geps (address-taken).
        foreach (var block in fn.Blocks)
        foreach (var instr in block.Instructions)
        {
            if (instr is PhiInstruction)
                phiTempBytes += instr.Type.SizeInBytes; // cycle-breaking may stage one temp per phi
            switch (instr)
            {
                case AllocaInstruction a:
                    staticAddr[a] = wram;
                    wram += a.Allocated.SizeInBytes;
                    break;
                case GetElementPtrInstruction g
                    when g.Index is IrConstInt ci
                        && staticAddr.TryGetValue(g.BasePointer, out int b):
                    staticAddr[g] = b + (int)ci.Value * g.ElementType.SizeInBytes;
                    break;
            }
        }

        // Colour the remaining SSA value slots (non-void results that aren't static addresses):
        // values whose live ranges never overlap share WRAM bytes.
        var colored = new HashSet<IrValue>(Eq);
        foreach (var block in fn.Blocks)
        foreach (var instr in block.Instructions)
            if (
                instr.Type.Kind != IrTypeKind.Void
                && instr is not AllocaInstruction
                && !staticAddr.ContainsKey(instr)
            )
                colored.Add(instr);

        // Assign CPU registers to a conservative set of short-lived values first; a resident value needs
        // no WRAM slot, so it is dropped from the colouring set before the interference graph is built.
        var register = allowResidency
            ? SelectResidents(fn, colored)
            : new Dictionary<IrValue, Sm83Register>(Eq);
        foreach (var resident in register.Keys)
            colored.Remove(resident);

        var interference = ComputeInterference(fn, colored);

        int colorBase = wram;
        int colorEnd = colorBase;

        var order = new List<IrValue>();
        foreach (var block in fn.Blocks)
        foreach (var instr in block.Instructions)
            if (colored.Contains(instr))
                order.Add(instr);

        foreach (var value in order)
        {
            int size = value.Type.SizeInBytes;
            int start = colorBase;
            bool placed = false;
            while (!placed)
            {
                placed = true;
                foreach (var neighbour in interference[value])
                    if (
                        slot.TryGetValue(neighbour, out int ns)
                        && Overlaps(start, size, ns, neighbour.Type.SizeInBytes)
                    )
                    {
                        start = ns + neighbour.Type.SizeInBytes; // move past the conflict, then recheck
                        placed = false;
                        break;
                    }
            }
            slot[value] = start;
            colorEnd = Math.Max(colorEnd, start + size);
        }

        wram = colorEnd;
        int phiTempBase = wram;
        wram += phiTempBytes; // cycle-breaking stages at most one temp per phi, sized to its type

        return new FunctionAllocation
        {
            Slot = slot,
            StaticAddr = staticAddr,
            Register = register,
            PhiTempBase = phiTempBase,
            FrameBase = baseAddr,
            FrameEnd = wram,
        };
    }

    // ---- Register residency (SOTA items #2, #5) ---------------------------
    //
    // Deliberately narrow so it is provably correct on an accumulator machine whose emitters freely clobber
    // registers. A value is register-resident *only* if it is produced by a "gentle" ALU op (ADD/SUB/AND/
    // OR/XOR, one or two bytes) and its whole live range — definition through last use, all in one basic
    // block — contains nothing but other gentle ALU ops. Those emitters route operands through A (and B for
    // a register operand) and touch nothing else, so a byte value in C/D/E or a 16-bit value in the HL pair
    // is provably untouched across its range. Because a resident's every use is a gentle op, it also never
    // reaches the ret/call/phi/memory emitters — its entire interaction is with EmitBinary.
    //
    // Two candidates in the same block interfere iff their live ranges overlap (the SSA def-point rule in
    // Interferes). Because every candidate is single-block and dies at its last use, candidates in different
    // blocks never interfere. Registers are full-width and physically distinct (C/D/E each one byte, HL two;
    // the byte set is disjoint from HL), so — unlike the WRAM colourer — there is no partial-slot-overlap
    // hazard and no need for the wide-result interference rule: values that do not overlap can always share
    // a register. That is what lets a chain coalesce (v2 = v1 + c: v1 dies exactly as v2 is born, so they do
    // not interfere and reuse the same register), including 16-bit chains in HL, since a full-register write
    // is low-byte-then-high and reads each source byte before overwriting it. Wider values (i32/i64) stay in
    // WRAM — no register room. B is reserved as the gentle path's ALU scratch, so it is never a resident.

    /// <summary>Registers a byte value may occupy, in preference order. B is excluded (the gentle ALU path
    /// uses it for a register operand); H and L are reserved for the HL pair.</summary>
    private static readonly Sm83Register[] ByteRegisters =
    [
        Sm83Register.E,
        Sm83Register.D,
        Sm83Register.C,
    ];

    /// <summary>ADD/SUB/AND/OR/XOR at 8- or 16-bit width — the ops whose emitter (<c>EmitBinary</c>'s
    /// straight path) touches only <c>A</c> and <c>B</c>. Shifts and mul/div/rem are excluded: they use
    /// E/D/BC/HL or call runtime routines that clobber everything.</summary>
    private static bool IsGentleBinary(IrInstruction instr) =>
        instr is BinaryInstruction b
        && b.Type.SizeInBytes is 1 or 2
        && b.Op
            is IrBinaryOp.Add
                or IrBinaryOp.Sub
                or IrBinaryOp.And
                or IrBinaryOp.Or
                or IrBinaryOp.Xor;

    /// <summary>One residency candidate: a gentle-ALU value whose live range is a single block.</summary>
    private readonly record struct Residency(
        IrValue Value,
        IrBasicBlock Block,
        int DefIndex,
        int LastUse
    );

    private static Dictionary<IrValue, Sm83Register> SelectResidents(
        IrFunction fn,
        HashSet<IrValue> colored
    )
    {
        // Gather candidates in program order.
        var candidates = new List<Residency>();
        foreach (var block in fn.Blocks)
        {
            var instrs = block.Instructions;
            for (int defIndex = 0; defIndex < instrs.Count; defIndex++)
            {
                var value = instrs[defIndex];
                if (
                    colored.Contains(value)
                    && IsGentleBinary(value)
                    && TryResidencyRange(fn, block, defIndex, out int lastUse)
                )
                    candidates.Add(new Residency(value, block, defIndex, lastUse));
            }
        }

        // Greedy register assignment. A byte value takes the first free C/D/E; a 16-bit value takes HL.
        // "Free" means not held by an already-assigned candidate whose range overlaps this one.
        var register = new Dictionary<IrValue, Sm83Register>(Eq);
        var assigned = new List<(Residency Cand, Sm83Register Reg)>();

        foreach (var cand in candidates)
        {
            var pool = cand.Value.Type.SizeInBytes == 1 ? ByteRegisters : [Sm83Register.Hl];
            foreach (var reg in pool)
            {
                bool free = true;
                foreach (var (other, otherReg) in assigned)
                    if (otherReg == reg && Interferes(cand, other))
                    {
                        free = false;
                        break;
                    }
                if (free)
                {
                    register[cand.Value] = reg;
                    assigned.Add((cand, reg));
                    break;
                }
            }
        }

        return register;
    }

    /// <summary>Two single-block candidates interfere iff one is live just after the other's definition
    /// (the standard SSA def-point interference test). A value is live-out of instruction <c>i</c> when it
    /// is defined at or before <c>i</c> and used after <c>i</c>; the def == last-use boundary (a value that
    /// dies exactly as the other is born) is therefore not interference, so the two may share a register.</summary>
    private static bool Interferes(Residency a, Residency b)
    {
        if (!ReferenceEquals(a.Block, b.Block))
            return false; // each dies at its last use in its own block; never simultaneously live
        bool aLiveAfterBDef = a.DefIndex <= b.DefIndex && a.LastUse > b.DefIndex;
        bool bLiveAfterADef = b.DefIndex <= a.DefIndex && b.LastUse > a.DefIndex;
        return aLiveAfterBDef || bLiveAfterADef;
    }

    /// <summary>Whether the value at <paramref name="defIndex"/> of <paramref name="block"/> has all its
    /// uses inside its own block and every instruction from just after its definition through its last use
    /// is a gentle ALU op — so a register holding it is never clobbered across its live range. Outputs the
    /// index of the last in-block use.</summary>
    private static bool TryResidencyRange(
        IrFunction fn,
        IrBasicBlock block,
        int defIndex,
        out int lastUse
    )
    {
        var value = block.Instructions[defIndex];
        lastUse = -1;

        // Any use outside this block, or any use as a phi edge value, escapes the single-block window.
        foreach (var b in fn.Blocks)
        {
            var instrs = b.Instructions;
            for (int i = 0; i < instrs.Count; i++)
            {
                var instr = instrs[i];
                if (instr is PhiInstruction phi)
                {
                    foreach (var (incoming, _) in phi.Incomings)
                        if (ReferenceEquals(incoming, value))
                            return false; // used on a control-flow edge
                    continue;
                }
                foreach (var operand in instr.Operands)
                    if (ReferenceEquals(operand, value))
                    {
                        if (!ReferenceEquals(b, block))
                            return false; // used in another block
                        lastUse = Math.Max(lastUse, i);
                    }
            }
        }

        if (lastUse < 0)
            return false; // no in-block use: nothing to keep resident for

        // Every instruction strictly after the definition, up to and including the last use, must be
        // gentle — those are the only instructions that run while the value is live.
        for (int i = defIndex + 1; i <= lastUse; i++)
            if (!IsGentleBinary(block.Instructions[i]))
                return false;

        return true;
    }

    private static bool Overlaps(int s1, int z1, int s2, int z2) => s1 < s2 + z2 && s2 < s1 + z1;

    /// <summary>
    /// Live-range interference over the coloured values. Two values interfere if they can be
    /// simultaneously live. Liveness comes from the shared <see cref="IrLiveness"/> analysis (SSA
    /// backward dataflow, phi-edge semantics); this method filters it to the coloured set and layers on
    /// the backend-specific interference rules (a wide result and a wide phi must not partially overlap
    /// their operands' slots — see the byte-by-byte emit note below).
    /// </summary>
    private static Dictionary<IrValue, HashSet<IrValue>> ComputeInterference(
        IrFunction fn,
        HashSet<IrValue> colored
    )
    {
        var liveness = IrLiveness.Compute(fn);

        var graph = new Dictionary<IrValue, HashSet<IrValue>>(Eq);
        foreach (var v in colored)
            graph[v] = new HashSet<IrValue>(Eq);
        void Interfere(IrValue a, IrValue b)
        {
            if (!ReferenceEquals(a, b))
            {
                graph[a].Add(b);
                graph[b].Add(a);
            }
        }

        foreach (var b in fn.Blocks)
        {
            // Seed the backward walk from the coloured values live on block exit (IrLiveness tracks
            // every trackable value; the WRAM colourer only cares about the ones it colours).
            var live = new HashSet<IrValue>(Eq);
            foreach (var v in liveness.LiveOut(b))
                if (colored.Contains(v))
                    live.Add(v);

            var blockPhis = new List<PhiInstruction>();
            for (int i = b.Instructions.Count - 1; i >= 0; i--)
            {
                var instr = b.Instructions[i];
                if (instr is PhiInstruction phi)
                {
                    blockPhis.Add(phi);
                    continue;
                }
                if (colored.Contains(instr))
                {
                    foreach (var w in live)
                        Interfere(instr, w);
                    live.Remove(instr);
                }
                // A multi-byte result is emitted byte-by-byte in place (see EmitBinary/EmitConv/
                // EmitShift), so it must not share a slot with any operand it reads: a partial
                // overlap would clobber a source byte before it is read. Force disjointness by
                // interfering the result with its colored operands. (An i8 result reads/writes a
                // single byte, so full-coincidence or disjoint are both safe — leave it free to
                // coalesce with a dying operand.)
                bool wideResult = colored.Contains(instr) && instr.Type.SizeInBytes >= 2;
                foreach (var op in instr.Operands)
                    if (colored.Contains(op))
                    {
                        if (wideResult)
                            Interfere(instr, op);
                        live.Add(op);
                    }
            }

            foreach (var p in blockPhis)
            foreach (var w in live)
                Interfere(p, w);
            for (int i = 0; i < blockPhis.Count; i++)
            for (int j = i + 1; j < blockPhis.Count; j++)
                Interfere(blockPhis[i], blockPhis[j]);

            // A wide phi is realized as a byte-by-byte parallel copy from each incoming value on the
            // predecessor edge; like a wide result versus its operands, it must not partially overlap
            // an incoming's slot, or the low→high copy clobbers a source byte before reading it. Force
            // disjointness by interfering a wide phi with its colored incomings.
            foreach (var p in blockPhis)
                if (p.Type.SizeInBytes >= 2)
                    foreach (var (val, _) in p.Incomings)
                        if (colored.Contains(val))
                            Interfere(p, val);
        }

        return graph;
    }
}
