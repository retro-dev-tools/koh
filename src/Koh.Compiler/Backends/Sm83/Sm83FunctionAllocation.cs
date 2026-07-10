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
/// A conservative set of short-lived values and leaf parameters may instead be held in a CPU register
/// (<see cref="Register"/>) rather than a WRAM slot — register residency and the register calling
/// convention (SOTA items #2/#4/#5). See <see cref="SelectResidents"/> for the candidate rule.
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

    /// <param name="allowResidency">Whether short-lived instruction results may be assigned CPU registers.
    /// Disabled for interrupt handlers and recursive functions, whose prologues/epilogues and frame-save
    /// paths impose register constraints the conservative residency model does not yet reason about.</param>
    /// <param name="allowParamResidency">Whether parameters may be *received* in CPU registers (a register
    /// calling convention). The caller places the argument in the register instead of the WRAM param slot.
    /// Off for the entry function, which has no caller to set up its registers.</param>
    public static FunctionAllocation For(
        IrFunction fn,
        int baseAddr,
        bool allowResidency = false,
        bool allowParamResidency = false
    )
    {
        var staticAddr = new Dictionary<IrValue, int>(Eq);
        var slot = new Dictionary<IrValue, int>(Eq);
        int phiTempBytes = 0;

        // Structural pass: classify static-address instructions (allocas, and constant-index geps rooted at
        // one) and count phi cycle-breaking temps. Addresses come after residency is decided, since a
        // resident param/value takes no WRAM.
        var staticSet = new HashSet<IrValue>(Eq);
        foreach (var block in fn.Blocks)
        foreach (var instr in block.Instructions)
        {
            if (instr is PhiInstruction)
                phiTempBytes += instr.Type.SizeInBytes; // cycle-breaking may stage one temp per phi
            switch (instr)
            {
                case AllocaInstruction:
                    staticSet.Add(instr);
                    break;
                case GetElementPtrInstruction g
                    when g.Index is IrConstInt && staticSet.Contains(g.BasePointer):
                    staticSet.Add(g);
                    break;
            }
        }

        // Values eligible for a WRAM slot: non-void results that are not static addresses.
        var colored = new HashSet<IrValue>(Eq);
        foreach (var block in fn.Blocks)
        foreach (var instr in block.Instructions)
            if (
                instr.Type.Kind != IrTypeKind.Void
                && instr is not AllocaInstruction
                && !staticSet.Contains(instr)
            )
                colored.Add(instr);

        // Assign CPU registers to a conservative set of short-lived values and (leaf) parameters; a
        // resident value/param needs no WRAM slot.
        var register = SelectResidents(fn, colored, allowResidency, allowParamResidency);
        foreach (var resident in register.Keys)
            colored.Remove(resident);

        // Lay out WRAM: parameters first (a stable ABI — the caller writes them here), skipping any
        // received in a register; then alloca/gep static storage; then the coloured values below.
        int wram = baseAddr;
        foreach (var p in fn.Parameters)
        {
            if (register.ContainsKey(p))
                continue; // received in a register
            slot[p] = wram;
            wram += p.Type.SizeInBytes;
        }

        foreach (var block in fn.Blocks)
        foreach (var instr in block.Instructions)
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

    // ---- Register residency (SOTA items #2, #4, #5) -----------------------
    //
    // Deliberately narrow so it is provably correct on an accumulator machine whose emitters freely clobber
    // registers. A value is register-resident *only* if it is produced by a "gentle" ALU op (ADD/SUB/AND/
    // OR/XOR, one or two bytes) and its whole live range — definition through last use, all in one basic
    // block — contains nothing but other gentle ALU ops. Those emitters route operands through A (and B for
    // a register operand) and touch nothing else, so a byte value in C/D/E/H/L or a 16-bit value in the HL
    // pair is provably untouched across its range. Because a resident's every use is a gentle op, it also
    // never reaches the ret/call/phi/memory emitters — its entire interaction is with EmitBinary.
    //
    // A parameter is also a candidate (item #4, the register calling convention): if all its uses are a
    // gentle prefix of the entry block, it is *received* in a register — the caller places the argument
    // there (EmitCall) instead of writing the WRAM param slot, and the callee keeps it resident. A
    // parameter is live from entry, so its whole entry prefix up to its last use must be gentle.
    //
    // Two candidates conflict iff they physically overlap AND their live ranges overlap. Physical overlap
    // is bitwise (C/D/E are distinct bytes; a byte in L or H overlaps the HL pair — item #5's bytewise H:L
    // allocation). Live-range overlap is the SSA def-point rule (Interferes); since every candidate is
    // single-block and dies at its last use, candidates in different blocks never overlap. Registers are
    // full-width, so — unlike the WRAM colourer — there is no partial-slot-overlap hazard and no need for
    // the wide-result interference rule: values that do not overlap can always share a register. That is
    // what lets a chain coalesce (v2 = v1 + c: v1 dies exactly as v2 is born, so they do not interfere and
    // reuse the same register), including 16-bit chains in HL, since a full-register write is low-byte-then-
    // high and reads each source byte before overwriting it. Wider values (i32/i64) stay in WRAM — no
    // register room. B is reserved as the gentle path's ALU scratch, so it is never a resident.

    /// <summary>Registers a byte value may occupy, in preference order. B is excluded (the gentle ALU path
    /// uses it for a register operand). H and L come last: a byte only takes half of the HL pair under
    /// register pressure, leaving HL free for 16-bit residents when it can. A byte in H or L physically
    /// aliases an HL pair, which the bitwise-overlap interference test below accounts for — so this is the
    /// bytewise H:L allocation (Krause SCOPES 2015): L can hold a byte while H holds another value.</summary>
    private static readonly Sm83Register[] ByteRegisters =
    [
        Sm83Register.E,
        Sm83Register.D,
        Sm83Register.C,
        Sm83Register.L,
        Sm83Register.H,
    ];

    /// <summary>ADD/SUB/AND/OR/XOR at 8- or 16-bit width — the ops whose emitter (<c>EmitBinary</c>'s
    /// straight path) touches only <c>A</c> and <c>B</c>. Shifts and mul/div/rem are excluded: they use
    /// E/D/BC/HL or call runtime routines that clobber everything.
    /// <para>Load-bearing invariant: a gentle op is same-width, so a byte value can never be an operand of
    /// a 16-bit op (that needs a non-gentle <c>Conv</c>). That is what makes the bytewise H:L aliasing safe
    /// — a byte parked in <c>L</c> is never read by an op that writes the whole <c>HL</c> pair. Do not add
    /// conversions or mixed-width ops here without revisiting that.</para></summary>
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
        HashSet<IrValue> colored,
        bool allowResidency,
        bool allowParamResidency
    )
    {
        var register = new Dictionary<IrValue, Sm83Register>(Eq);
        if (!allowResidency && !allowParamResidency)
            return register;

        var uses = BuildUseMap(fn); // one pass; consulted per candidate below
        var entry = fn.EntryBlock;

        // Gather candidates in program order. Parameters come first: they are the calling convention, so
        // they claim registers ahead of any body value (they are all live from entry, so they interfere
        // with each other and take distinct registers).
        var candidates = new List<Residency>();

        // A parameter is live from function entry and is never redefined, so if the entry block can be
        // re-entered (a loop header) it must survive the whole block, not just the gentle prefix up to its
        // last use — a non-gentle op *after* that use would clobber its register before the next iteration
        // re-reads it. Only allow parameter residency when the entry block has no predecessor. (A body
        // value has no such hazard: it is redefined at its def each iteration.)
        if (allowParamResidency && entry is not null && !HasPredecessor(fn, entry))
            foreach (var p in fn.Parameters)
                if (
                    p.Type.SizeInBytes is 1 or 2
                    && uses.TryGetValue(p, out var u)
                    && !u.PhiUse
                    && !u.MultiBlock
                    && ReferenceEquals(u.Block, entry)
                    && GentleRange(entry, 0, u.LastUse) // parameter is live from entry: prefix [0, lastUse]
                )
                    candidates.Add(new Residency(p, entry, -1, u.LastUse));

        if (allowResidency)
            foreach (var block in fn.Blocks)
            {
                var instrs = block.Instructions;
                for (int defIndex = 0; defIndex < instrs.Count; defIndex++)
                {
                    var value = instrs[defIndex];
                    if (
                        colored.Contains(value)
                        && IsGentleBinary(value)
                        && uses.TryGetValue(value, out var u)
                        && !u.PhiUse
                        && !u.MultiBlock
                        && ReferenceEquals(u.Block, block)
                        && GentleRange(block, defIndex + 1, u.LastUse)
                    )
                        candidates.Add(new Residency(value, block, defIndex, u.LastUse));
                }
            }

        // Greedy register assignment. A byte value takes the first free C/D/E; a 16-bit value takes HL.
        // "Free" means not held by an already-assigned candidate whose range overlaps this one.
        var assigned = new List<(Residency Cand, Sm83Register Reg)>();

        foreach (var cand in candidates)
        {
            var pool = cand.Value.Type.SizeInBytes == 1 ? ByteRegisters : [Sm83Register.Hl];
            foreach (var reg in pool)
            {
                bool free = true;
                foreach (var (other, otherReg) in assigned)
                    // Physical overlap is bitwise: E&D == 0 (distinct), but L&HL == L (a byte in L aliases
                    // the HL pair). Two candidates conflict only if they physically overlap AND their live
                    // ranges do.
                    if ((otherReg & reg) != 0 && Interferes(cand, other))
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

    /// <summary>Where a value is used, gathered in one pass so residency selection does not re-scan the
    /// whole function per candidate. <see cref="Block"/>/<see cref="LastUse"/> track the (single) block a
    /// value is used in and the last index there; <see cref="MultiBlock"/> is set if it is used in a second
    /// block, and <see cref="PhiUse"/> if it appears as a phi's incoming (a control-flow-edge use). A value
    /// is single-block-resident-eligible iff <c>!PhiUse &amp;&amp; !MultiBlock</c> and its home block matches.</summary>
    private sealed class UseInfo
    {
        public bool PhiUse;
        public bool MultiBlock;
        public IrBasicBlock? Block;
        public int LastUse = -1;
    }

    private static Dictionary<IrValue, UseInfo> BuildUseMap(IrFunction fn)
    {
        var map = new Dictionary<IrValue, UseInfo>(Eq);
        UseInfo Info(IrValue v) => map.TryGetValue(v, out var info) ? info : map[v] = new UseInfo();

        foreach (var b in fn.Blocks)
        {
            var instrs = b.Instructions;
            for (int i = 0; i < instrs.Count; i++)
            {
                var instr = instrs[i];
                if (instr is PhiInstruction phi)
                {
                    foreach (var (incoming, _) in phi.Incomings)
                        Info(incoming).PhiUse = true; // phi operands are control-flow-edge uses
                    continue;
                }
                foreach (var operand in instr.Operands)
                {
                    var info = Info(operand);
                    if (info.Block is null)
                        (info.Block, info.LastUse) = (b, i);
                    else if (ReferenceEquals(info.Block, b))
                        info.LastUse = i; // a later index in the same block
                    else
                        info.MultiBlock = true; // used in a second block
                }
            }
        }
        return map;
    }

    /// <summary>Whether every instruction in <c>[start, lastUse]</c> of <paramref name="block"/> is a gentle
    /// ALU op — so a register holding a value live across that span is never clobbered.</summary>
    private static bool GentleRange(IrBasicBlock block, int start, int lastUse)
    {
        for (int i = start; i <= lastUse; i++)
            if (!IsGentleBinary(block.Instructions[i]))
                return false;
        return true;
    }

    /// <summary>Whether any block branches to <paramref name="target"/> (it is a re-entry / loop header).</summary>
    private static bool HasPredecessor(IrFunction fn, IrBasicBlock target)
    {
        foreach (var b in fn.Blocks)
            if (b.Terminator?.Successors is { } successors)
                foreach (var s in successors)
                    if (ReferenceEquals(s, target))
                        return true;
        return false;
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
