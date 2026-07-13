using Koh.Compiler.Backends.Sm83.Mir;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Analysis;
using Koh.Compiler.Ir.Optimization;

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

    /// <summary>Per Layer-1 loop-induction residency candidate, the one-time register load a preheader
    /// block must emit (the init value, into the phi's register) right before its branch to the loop
    /// header — see <see cref="SelectLoopInductionResidents"/>. Empty unless a candidate was admitted.</summary>
    public required Dictionary<
        IrBasicBlock,
        List<LoopInductionSync>
    > LoopInductionPreheaderSync { get; init; }

    /// <summary>Layer 2 (stride-1 pointer residency): the single dereferencing <see cref="LoadInstruction"/>
    /// or <see cref="StoreInstruction"/> a residency-admitted pointer local's fused post-increment fires
    /// on, keyed by that instruction, valued by the register (<see cref="Sm83Register.Hl"/> or
    /// <see cref="Sm83Register.De"/>) the pointer is coalesced into — see
    /// <see cref="SelectLoopPointerResidents"/>. <see cref="MemoryEmitter"/> emits <c>ld a,(hl+)</c>/
    /// <c>ld (hl+),a</c> for an <c>Hl</c> entry, or <c>ld a,(de)</c>/<c>ld (de),a</c> plus an explicit
    /// <c>inc de</c> for a <c>De</c> entry, instead of the normal address-resolution path; the paired
    /// <c>gep</c> (the incremented pointer) is left with no <see cref="Slot"/> and no independent
    /// emission at all — its value is entirely the fused instruction's side effect.</summary>
    public required Dictionary<IrInstruction, Sm83Register> FusedPointerSite { get; init; }

    /// <summary>Layer 2's preheader sync: unlike <see cref="LoopInductionSync"/> (which loads an SSA
    /// value's bytes), a residency-admitted pointer local has no phi to read an "incoming value" from —
    /// C#'s pointer locals are never promoted to SSA by <c>Mem2RegPass</c> (arrays/structs/pointers are
    /// deliberately left in memory). The register's initial value must instead be read straight out of
    /// the local's own fixed WRAM home (its alloca's <see cref="StaticAddr"/> entry) — see
    /// <see cref="SelectLoopPointerResidents"/> and <c>EmitContext.LoadAddressContentsIntoRegisterPair</c>.</summary>
    public readonly record struct PointerHomeSync(AllocaInstruction Home, Sm83Register Reg);

    /// <summary>Per preheader block, the one-time register loads (from a pointer local's home WRAM
    /// address, not an SSA value) <see cref="FunctionEmitter"/> must emit right before that block's
    /// (unconditional) branch to the loop header it precedes. Empty unless a Layer 2 candidate was
    /// admitted. Kept separate from <see cref="LoopInductionPreheaderSync"/> because the two need
    /// different emission code (memory read vs. SSA-value read), even though both fire at the same
    /// injection point.</summary>
    public required Dictionary<
        IrBasicBlock,
        List<PointerHomeSync>
    > PointerHomePreheaderSync { get; init; }

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

        // Layer 2 (stride-1 pointer residency, SOTA item — see its own region below) runs BEFORE Layer 1:
        // a byte* loop-carried local (never SSA-promoted — Mem2RegPass leaves pointers in memory) shares
        // Hl or De with its stride-1 gep for the loop's duration, and its one dereferencing load/store
        // gets a fused post-increment opcode. It claims the two scarce register pairs first because
        // losing a pointer candidate means falling back to an expensive dynamic-gep multiply every
        // iteration, whereas losing a Layer 1 byte candidate (below) merely falls back to one WRAM
        // reload/store per iteration — the cheaper miss. Layer 1's own conflict check is bitwise (see
        // below), so it correctly steps around whatever bytes Layer 2 claims.
        var preheaderSync = new Dictionary<IrBasicBlock, List<LoopInductionSync>>(Eq);
        var (ptrRegister, ptrHomeSync, fusedSite) = SelectLoopPointerResidents(
            fn,
            register,
            staticSet
        );
        foreach (var (value, reg) in ptrRegister)
        {
            register[value] = reg;
            colored.Remove(value); // both the reload and its stride-1 gep are register-only (no dual)
        }

        // Layer 1 (loop-carried induction residency): a byte induction phi in a simple counted loop and
        // its back-edge-defining gentle binary share one CPU register for the loop's duration. Unlike the
        // mechanism above, the *phi* keeps its WRAM slot (dual placement — see the region's header comment
        // for why); only the defining binary loses its slot.
        var (loopRegister, loopPreheaderSync) = SelectLoopInductionResidents(fn, register);
        foreach (var (value, reg) in loopRegister)
        {
            register[value] = reg;
            if (value is not PhiInstruction)
                colored.Remove(value); // the phi stays coloured (dual); its body value is register-only
        }
        foreach (var (block, syncs) in loopPreheaderSync)
        {
            if (!preheaderSync.TryGetValue(block, out var existing))
                preheaderSync[block] = existing = new List<LoopInductionSync>();
            existing.AddRange(syncs);
        }

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
            LoopInductionPreheaderSync = preheaderSync,
            FusedPointerSite = fusedSite,
            PointerHomePreheaderSync = ptrHomeSync,
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

    // ---- Loop-induction register residency (Layer 1) ----------------------
    //
    // A byte loop-carried induction phi — the classic `for`/`while` counter — round-trips WRAM twice
    // every iteration under the plain colourer: once to reload it for the compare/body, once to write
    // the next value back through the phi's back-edge copy. This residency overlay keeps the value in a
    // CPU register for the loop's duration instead.
    //
    // Unlike SelectResidents above, this is *not* single-block: the phi lives in the header (compare),
    // its next value is computed in a tail block (the gentle binary on the back edge), and either may be
    // read again after the loop exits. The design deliberately does NOT touch ControlFlowEmitter.cs (out
    // of this package's file ownership), which means two things are non-negotiable:
    //
    //   1. The phi KEEPS its WRAM slot (dual placement, unlike an ordinary resident). EmitPhiCopies
    //      (ControlFlowEmitter) indexes `ctx.Slot[phi]` unconditionally for every predecessor edge — if
    //      the phi had no slot entry that throws. So every edge into the header still writes the phi's
    //      slot exactly as today: the preheader edge writes the init value, the back-edge writes the
    //      loop-computed value. That back-edge store is a real, unavoidable per-iteration WRAM write
    //      (do not chase eliminating it — see the module's design notes); what this pass removes is the
    //      per-iteration *reload*, by coalescing the phi and its defining binary onto the SAME physical
    //      register, so a write to one is instantaneously visible to a read of the other with no copy at
    //      all — the read side of "the back-edge copy becomes a no-op".
    //   2. The defining binary in the tail (the "body value") has NO slot at all — like an ordinary
    //      resident, it is register-only — so its own result-store (EmitBinary's existing
    //      `StoreResultByte`, unmodified) already writes straight to that shared register with zero new
    //      emitter code.
    //
    // What IS new, and lives in FunctionEmitter.cs (owned): the register needs the *init* value loaded
    // into it once, on the preheader edge, before the header's phis are entered — nothing else in the
    // function ever writes it except the tail's own gentle binary. See
    // `FunctionAllocation.LoopInductionPreheaderSync` and FunctionEmitter's `EmitInstruction` for a
    // `BrInstruction` whose block has a pending sync.
    //
    // Safety hinges entirely on admission: a resident register is only correct to read from ANYWHERE
    // (LoadByteToA does not know which block it is called from) if NOTHING across the phi's and body
    // value's *entire* live range — computed via the same whole-function IrLiveness used by
    // ComputeInterference, not just the loop body — can clobber it. That is "GentleRange" (used above
    // for a single block) generalized to an arbitrary block set: every block where either value is live
    // must contain only gentle binaries, the phi itself, an i8 compare, a Br/CondBr terminator, a void
    // return, or a load/store through a literal constant pointer address (never a dynamic/gep address —
    // that needs HL/DE, which is Layer 2's job and would corrupt a resident held in D or E). The audited
    // exception is EmitCompare's Eq/Ne path, which always uses C as ALU scratch regardless of operand
    // kind (unlike Ult/Uge, which only touches B and only for a non-constant right operand) — so C is
    // pulled from the candidate pool whenever an Eq/Ne compare is live in the same range.
    //
    // Scope, deliberately conservative for a first correct cut (documented, not silently dropped):
    //   - i8 only (register room; i16 would need a whole free HL, contended with Layer 2's pointers).
    //   - The loop's preheader must end in a plain, unconditional `br` to the header (so the injection
    //     point in FunctionEmitter is unconditionally reached — a guarded preheader, e.g. `if (n > 0)
    //     { for (...) }`, is not yet admitted).
    //   - No early `ret` inside the admitted block set (EmitRet can touch HL/DE for a non-void result,
    //     which would clobber a resident in D/E; a void return is fine and explicitly allowed).
    //   - A register chosen here is reserved for the whole function once assigned (no attempt to share
    //     it with an unrelated SelectResidents candidate in a disjoint block) — simple and safe, at the
    //     cost of some missed coalescing in functions with many independent short chains.

    /// <summary>One preheader-edge register load a residency-admitted loop needs: on the edge into
    /// <c>Reg</c>'s owning header, before entering the header at all, load <see cref="Init"/> (the
    /// phi's incoming value from that specific preheader) into <see cref="Reg"/>. Every other write to
    /// the register comes from the loop body's own gentle binary re-using the same physical register.</summary>
    public readonly record struct LoopInductionSync(IrValue Init, Sm83Register Reg);

    /// <summary>The loop-pool registers, preferred order, when no Eq/Ne compare is live in range (see
    /// the region header comment for the audit). B and A are never candidates (B is the gentle path's
    /// register-operand scratch; A is the accumulator). H/L are excluded — Layer 1 never admits a
    /// dynamic-address load/store, but a byte resident in H or L would still collide with any future
    /// pointer residency (Layer 2) sharing this same function.</summary>
    private static readonly Sm83Register[] LoopBytePool =
    [
        Sm83Register.C,
        Sm83Register.D,
        Sm83Register.E,
    ];

    /// <summary>The loop pool with C withheld, for a candidate whose live range includes an Eq/Ne
    /// compare (EmitCompare's equality path always clobbers C, constant operand or not).</summary>
    private static readonly Sm83Register[] LoopBytePoolNoC = [Sm83Register.D, Sm83Register.E];

    private static bool IsEqNeCompare(IrInstruction instr) =>
        instr is CompareInstruction { Op: IrCompareOp.Eq or IrCompareOp.Ne };

    /// <summary>Whether <paramref name="v"/> is a literal constant pointer address (e.g. <c>(byte*)
    /// 0xFF40</c>) — the only pointer shape Layer 1 admits for a load/store in the resident's live
    /// range. An alloca/gep static address or a global is deliberately NOT included here: recognizing
    /// those needs the module's <c>Globals</c> table, which is not available at this per-function,
    /// pre-EmitContext stage — a real but documented scope limit (TODO: thread globals through
    /// <see cref="For"/> to widen this if a hot loop needs it).</summary>
    private static bool IsLiteralPointerConst(IrValue v) =>
        v is IrConstInt c && v.Type.Kind == IrTypeKind.Pointer;

    /// <summary>Every instruction kind Layer 1 proves cannot clobber a resident register — see the
    /// region header comment. Applied uniformly to every block in a candidate's live range (header,
    /// tail, and any interior blocks alike), not just the loop body, so a use of the resident after the
    /// loop exits is exactly as safe as a use inside it.</summary>
    private static bool IsLoopSafeInstruction(IrInstruction instr) =>
        instr is PhiInstruction
        || IsGentleBinary(instr)
        || (instr is CompareInstruction cmp && cmp.Left.Type.SizeInBytes == 1)
        || (instr is LoadInstruction load && IsLiteralPointerConst(load.Pointer))
        || (instr is StoreInstruction store && IsLiteralPointerConst(store.Pointer))
        || instr is BrInstruction or CondBrInstruction
        || instr is RetInstruction { Value: null };

    /// <summary>Every block where <paramref name="a"/> or <paramref name="b"/> is live, by the same
    /// whole-function <see cref="IrLiveness"/> dataflow <see cref="ComputeInterference"/> uses — not
    /// just the natural loop's own block set, so a use after the loop exits is included.</summary>
    private static HashSet<IrBasicBlock> LiveBlocksOf(
        IrFunction fn,
        IrLiveness liveness,
        IrValue a,
        IrValue b
    )
    {
        var blocks = new HashSet<IrBasicBlock>(Eq);
        foreach (var block in fn.Blocks)
            if (
                liveness.LiveIn(block).Contains(a)
                || liveness.LiveOut(block).Contains(a)
                || liveness.LiveIn(block).Contains(b)
                || liveness.LiveOut(block).Contains(b)
            )
                blocks.Add(block);
        if (a is IrInstruction ai)
            blocks.Add(ai.Parent!);
        if (b is IrInstruction bi)
            blocks.Add(bi.Parent!);
        return blocks;
    }

    /// <summary>Whether <paramref name="header"/> holds a simple counted-loop shape for
    /// <paramref name="phi"/>: exactly two incomings, one from a block the header dominates (a back
    /// edge — the standard natural-loop test reused from <c>LoopInvariantCodeMotionPass.FindNaturalLoops</c>
    /// and <see cref="Dominators"/>, specialised to a single phi instead of a whole loop discovery pass)
    /// whose value is a gentle binary over the phi and a constant, and one from a genuinely external
    /// block (the preheader) ending in a plain, unconditional branch straight to the header.</summary>
    private static bool TryFindLoopShape(
        Dominators dom,
        IrBasicBlock header,
        PhiInstruction phi,
        out IrBasicBlock tail,
        out BinaryInstruction bodyValue,
        out IrBasicBlock preheader
    )
    {
        tail = null!;
        bodyValue = null!;
        preheader = null!;
        if (phi.Incomings.Count != 2)
            return false;

        (IrValue Value, IrBasicBlock Block)? tailIncoming = null;
        (IrValue Value, IrBasicBlock Block)? preheaderIncoming = null;
        foreach (var incoming in phi.Incomings)
        {
            if (dom.Dominates(header, incoming.Block))
                tailIncoming = incoming;
            else
                preheaderIncoming = incoming;
        }
        if (tailIncoming is not { } t || preheaderIncoming is not { } p)
            return false;

        if (
            t.Value is not BinaryInstruction binary
            || !IsGentleBinary(binary)
            || !ReferenceEquals(binary.Parent, t.Block)
            || !(
                (ReferenceEquals(binary.Left, phi) && binary.Right is IrConstInt)
                || (ReferenceEquals(binary.Right, phi) && binary.Left is IrConstInt)
            )
        )
            return false;

        // The preheader must reach the header unconditionally — FunctionEmitter injects the register's
        // one-time init load right before this exact branch, so the edge must be unguarded (see the
        // region header comment's scope note).
        if (
            p.Block.Terminator is not BrInstruction { Target: var target }
            || !ReferenceEquals(target, header)
        )
            return false;

        tail = t.Block;
        bodyValue = binary;
        preheader = p.Block;
        return true;
    }

    /// <summary>Select Layer-1 loop-induction residency candidates: see the region header comment for
    /// the full design. Returns the extra register assignments (folded into the caller's <c>register</c>
    /// dictionary — the phi dual-placed, its body value register-only) and the per-preheader init loads
    /// <see cref="FunctionEmitter"/> must emit.</summary>
    private static (
        Dictionary<IrValue, Sm83Register> Register,
        Dictionary<IrBasicBlock, List<LoopInductionSync>> PreheaderSync
    ) SelectLoopInductionResidents(IrFunction fn, Dictionary<IrValue, Sm83Register> assigned)
    {
        var register = new Dictionary<IrValue, Sm83Register>(Eq);
        var preheaderSync = new Dictionary<IrBasicBlock, List<LoopInductionSync>>(Eq);
        if (fn.EntryBlock is null || fn.Blocks.Count < 2)
            return (register, preheaderSync);

        var dom = new Dominators(fn);
        var liveness = IrLiveness.Compute(fn);

        foreach (var header in fn.Blocks)
        foreach (var instr in header.Instructions)
        {
            if (instr is not PhiInstruction phi || phi.Type.SizeInBytes != 1)
                continue;
            if (assigned.ContainsKey(phi) || register.ContainsKey(phi))
                continue;
            if (!TryFindLoopShape(dom, header, phi, out _, out var bodyValue, out var preheader))
                continue;
            if (assigned.ContainsKey(bodyValue) || register.ContainsKey(bodyValue))
                continue;

            var liveBlocks = LiveBlocksOf(fn, liveness, phi, bodyValue);
            if (!liveBlocks.All(b => b.Instructions.All(IsLoopSafeInstruction)))
                continue;

            bool hasEqNe = liveBlocks.Any(b => b.Instructions.Any(IsEqNeCompare));
            var pool = hasEqNe ? LoopBytePoolNoC : LoopBytePool;

            // Bitwise, not exact-value: Layer 2 may have already claimed Hl or De (whose bits are H/L or
            // D/E), and a lone byte in one of those bits would otherwise go undetected by an exact match.
            Sm83Register? chosen = null;
            foreach (var candidate in pool)
                if (
                    assigned.Values.All(r => (r & candidate) == 0)
                    && register.Values.All(r => (r & candidate) == 0)
                )
                {
                    chosen = candidate;
                    break;
                }
            if (chosen is not { } reg)
                continue;

            register[phi] = reg;
            register[bodyValue] = reg;

            var init = phi.Incomings.First(i => ReferenceEquals(i.Block, preheader)).Value;
            if (!preheaderSync.TryGetValue(preheader, out var syncs))
                preheaderSync[preheader] = syncs = new List<LoopInductionSync>();
            syncs.Add(new LoopInductionSync(init, reg));
        }

        return (register, preheaderSync);
    }

    // ---- Loop-pointer register residency (Layer 2) -------------------------
    //
    // The pointer analogue of Layer 1 — but pointer LOCALS have no phi to hang on: Mem2RegPass promotes
    // only integer-scalar allocas ("arrays, structs, and pointers are left in memory" — its own doc
    // comment), so `byte* p` in a loop compiles to a genuine reload/store round trip through `p`'s fixed
    // WRAM home every iteration: `ld = load p_home; ...use ld...; step = gep ld, +1; store step, p_home`.
    // This region recognises exactly that shape and keeps the pointer resident in Hl or De across the
    // loop instead, fusing its one dereference with the hardware's post-increment addressing: `ld a,(hl+)`
    // (0x2A) / `ld (hl+),a` (0x22) for Hl, or `ld a,(de)`/`ld (de),a` (0x1A/0x12) plus an explicit
    // `inc de` (0x13) for De — the SM83 has no `(de+)` mode. Two candidates in the same loop share the
    // pool: Hl (one opcode) is cheaper than De (two), so a load candidate claims it first.
    //
    // Placement differs from Layer 1 in a way that turns out simpler, not harder: `ld` (the reload) and
    // `step` (the gep) both go register-only, no dual slot — but the round trip's FINAL instruction, the
    // `store step, p_home`, is left as a completely ordinary, unmodified emission. Once `step` is
    // register-resident, EmitStore's existing static-address path already sources its value from the
    // register (LoadByteToA checks Register before Slot) and writes the fixed WRAM home exactly as before
    // — so the home slot is refreshed every iteration for free, with no exit-edge sync needed at all:
    // anything that reads `p` again later (even immediately after the loop) does so through a brand new
    // Load instruction at that program point (pointer locals are never SSA — every textual read of `p` is
    // its own reload), which just reads the always-current WRAM home normally. The register's lifetime is
    // therefore entirely bounded inside the loop; nothing outside it ever needs to know the register exists.
    //
    // Admission (conservative, "when in doubt, disqualify" throughout):
    //   - A simple loop shape: header has exactly two predecessors, one it dominates (tail, the back
    //     edge) and one it does not (preheader), and the preheader ends in a plain unconditional `br` —
    //     the same shape TryFindLoopShape uses, minus the phi (there is none here).
    //   - Within the loop body (header/tail plus any block between them - CollectLoopBody's standard
    //     natural-loop walk over predecessors), a `load` of a pointer-typed, fixed-WRAM-address
    //     (`staticSet`-classified — an alloca or a constant-index gep off one) local, with exactly two
    //     uses: one dereference (a Load/Store elsewhere whose Pointer operand IS the reload) and one
    //     `gep(reload, +1)` with a single-byte element, whose OWN only use is a `store` writing it back to
    //     that exact same fixed address. A non-constant index, non-unit stride, multi-byte element, or
    //     `p--`'s constant -1 are all out of Layer 2's scope (documented, not silently dropped).
    //   - Every OTHER instruction in the loop body must be one IsPointerLoopSafeInstruction proves cannot
    //     clobber Hl/De (gentle ops/compares — both provably A/B/C-only — phis, Br/CondBr, a void ret, a
    //     literal-constant-address load/store, or one of this batch's own reloads/fused sites/steps/
    //     storebacks) — in particular, any OTHER load/store/gep touching the SAME fixed address falls
    //     through unrecognised and disqualifies the candidate, so a reset like `if (x) { p = original; }`
    //     inside the loop is never silently missed.
    //   - A free register pair, checked by *bitwise* overlap against every already-assigned register in
    //     the function (not an exact-value check) — a lone byte resident in L or H would otherwise
    //     silently alias Hl.

    /// <summary>Same header/back-edge/preheader shape as <see cref="TryFindLoopShape"/>, minus the phi
    /// (pointer locals are never SSA-promoted — see the region header comment).</summary>
    private static bool TryFindLoopEntry(
        Dominators dom,
        IrFunction fn,
        IrBasicBlock header,
        out IrBasicBlock tail,
        out IrBasicBlock preheader
    )
    {
        tail = null!;
        preheader = null!;
        IrBasicBlock? t = null;
        IrBasicBlock? p = null;
        foreach (var pred in dom.PredecessorsOf(header))
        {
            if (dom.Dominates(header, pred))
            {
                if (t is not null)
                    return false; // more than one back edge - not this simple shape
                t = pred;
            }
            else
            {
                if (p is not null)
                    return false; // more than one loop-external entry
                p = pred;
            }
        }
        if (t is null || p is null)
            return false;
        if (
            p.Terminator is not BrInstruction { Target: var target }
            || !ReferenceEquals(target, header)
        )
            return false;

        tail = t;
        preheader = p;
        return true;
    }

    /// <summary>The natural loop body of the back edge tail-&gt;header: header plus every block that can
    /// reach <paramref name="tail"/> without passing through it again — the standard predecessor walk
    /// (mirrors <c>LoopInvariantCodeMotionPass.CollectLoopBody</c>).</summary>
    private static HashSet<IrBasicBlock> CollectLoopBody(
        Dominators dom,
        IrBasicBlock tail,
        IrBasicBlock header
    )
    {
        var body = new HashSet<IrBasicBlock>(Eq) { header };
        var work = new Stack<IrBasicBlock>();
        if (body.Add(tail))
            work.Push(tail);
        while (work.Count > 0)
        {
            var block = work.Pop();
            if (ReferenceEquals(block, header))
                continue;
            foreach (var pred in dom.PredecessorsOf(block))
                if (body.Add(pred))
                    work.Push(pred);
        }
        return body;
    }

    /// <summary>One Layer 2 candidate discovered in <paramref name="body"/>: a fixed-address pointer
    /// local's reload/use/step/storeback round trip. See the region header comment for the shape.</summary>
    private static bool TryFindPointerReloadShape(
        HashSet<IrBasicBlock> body,
        HashSet<IrValue> staticSet,
        LoadInstruction reload,
        out GetElementPtrInstruction step,
        out IrInstruction deref,
        out bool isLoad,
        out StoreInstruction storeback
    )
    {
        step = null!;
        deref = null!;
        isLoad = false;
        storeback = null!;

        if (
            reload.Type.Kind != IrTypeKind.Pointer
            || reload.Type.SizeInBytes != 2
            || !staticSet.Contains(reload.Pointer)
        )
            return false;

        IrInstruction? foundDeref = null;
        bool foundIsLoad = false;
        GetElementPtrInstruction? foundStep = null;
        int derefCount = 0;

        foreach (var block in body)
        foreach (var instr in block.Instructions)
        {
            switch (instr)
            {
                case LoadInstruction l when ReferenceEquals(l.Pointer, reload):
                    foundDeref = l;
                    foundIsLoad = true;
                    derefCount++;
                    break;
                case StoreInstruction s when ReferenceEquals(s.Pointer, reload):
                    foundDeref = s;
                    foundIsLoad = false;
                    derefCount++;
                    break;
                case GetElementPtrInstruction g
                    when ReferenceEquals(g.BasePointer, reload)
                        && g.Index is IrConstInt { Value: 1 }
                        && g.ElementType.SizeInBytes == 1:
                    if (foundStep is not null)
                        return false; // more than one stride-1 step - ambiguous, decline
                    foundStep = g;
                    break;
            }
        }
        if (foundStep is not { } stepGep || derefCount != 1 || foundDeref is not { } derefInstr)
            return false;

        // The step's own only use must be a store writing it straight back to the reload's same fixed
        // address - anything else (used twice, used elsewhere, stored to a different home) is unsafe.
        StoreInstruction? foundStoreback = null;
        int stepUses = 0;
        foreach (var block in body)
        foreach (var instr in block.Instructions)
        foreach (var operand in instr.Operands)
            if (ReferenceEquals(operand, stepGep))
            {
                stepUses++;
                if (
                    instr is StoreInstruction sb
                    && ReferenceEquals(sb.Value, stepGep)
                    && ReferenceEquals(sb.Pointer, reload.Pointer)
                )
                    foundStoreback = sb;
            }
        if (stepUses != 1 || foundStoreback is null)
            return false;

        step = stepGep;
        deref = derefInstr;
        isLoad = foundIsLoad;
        storeback = foundStoreback;
        return true;
    }

    /// <summary>Every instruction kind Layer 2 proves cannot clobber a resident pointer pair, applied to
    /// every block of a candidate's loop body — mirrors <see cref="IsLoopSafeInstruction"/>, widened to
    /// any-width compares/gentle ops (both provably A/B/C-only) and to this batch's own reloads/fused
    /// sites/steps/storebacks (so a sibling candidate sharing the same loop body is recognised too).</summary>
    private static bool IsPointerLoopSafeInstruction(
        IrInstruction instr,
        HashSet<IrInstruction> reloads,
        HashSet<IrInstruction> fusedDerefs,
        HashSet<IrInstruction> steps,
        HashSet<IrInstruction> storebacks
    ) =>
        instr is PhiInstruction
        || IsGentleBinary(instr) // 1 or 2 bytes; touches only A/B
        || instr is CompareInstruction // any width; touches only A/B/C plus RtCmp* memory scratch
        || reloads.Contains(instr) // the designated reload itself: a no-op (register already holds it)
        || steps.Contains(instr) // the designated step gep: a no-op (its value is the fused site's result)
        || storebacks.Contains(instr) // the designated storeback: unmodified, sources from the register
        || (
            instr is LoadInstruction load
            && (IsLiteralPointerConst(load.Pointer) || fusedDerefs.Contains(instr))
        )
        || (
            instr is StoreInstruction store
            && (IsLiteralPointerConst(store.Pointer) || fusedDerefs.Contains(instr))
        )
        || instr is BrInstruction or CondBrInstruction
        || instr is RetInstruction { Value: null };

    private static (
        Dictionary<IrValue, Sm83Register> Register,
        Dictionary<IrBasicBlock, List<PointerHomeSync>> PreheaderSync,
        Dictionary<IrInstruction, Sm83Register> FusedSite
    ) SelectLoopPointerResidents(
        IrFunction fn,
        Dictionary<IrValue, Sm83Register> assigned,
        HashSet<IrValue> staticSet
    )
    {
        var register = new Dictionary<IrValue, Sm83Register>(Eq);
        var preheaderSync = new Dictionary<IrBasicBlock, List<PointerHomeSync>>(Eq);
        var fusedSite = new Dictionary<IrInstruction, Sm83Register>(
            ReferenceEqualityComparer.Instance
        );
        if (fn.EntryBlock is null || fn.Blocks.Count < 2)
            return (register, preheaderSync, fusedSite);

        var dom = new Dominators(fn);

        // Phase 1: gather every structurally-shaped candidate (loop shape + reload/step/deref/storeback
        // quadruple) independent of the whitelist/register-pool checks below.
        var candidates =
            new List<(
                LoadInstruction Reload,
                GetElementPtrInstruction Step,
                IrBasicBlock Preheader,
                IrInstruction Deref,
                bool IsLoad,
                StoreInstruction Storeback,
                HashSet<IrBasicBlock> Body
            )>();

        foreach (var header in fn.Blocks)
        {
            if (!TryFindLoopEntry(dom, fn, header, out var tail, out var preheader))
                continue;
            var body = CollectLoopBody(dom, tail, header);

            foreach (var block in body)
            foreach (var instr in block.Instructions)
            {
                if (instr is not LoadInstruction reload || assigned.ContainsKey(reload))
                    continue;
                // Only a plain alloca home is handled below (the preheader sync reads FunctionAllocation's
                // own StaticAddr for it); a constant-index gep off one is also staticSet-classified but not
                // admitted here yet (documented scope limit, not a silent drop - excluded before any state
                // mutation so a later bail-out can never leave a half-registered candidate behind).
                if (reload.Pointer is not AllocaInstruction)
                    continue;
                if (
                    !TryFindPointerReloadShape(
                        body,
                        staticSet,
                        reload,
                        out var step,
                        out var deref,
                        out bool isLoad,
                        out var storeback
                    )
                )
                    continue;
                if (assigned.ContainsKey(step))
                    continue;

                candidates.Add((reload, step, preheader, deref, isLoad, storeback, body));
            }
        }

        if (candidates.Count == 0)
            return (register, preheaderSync, fusedSite);

        // Phase 2: admit whole LOOPS at a time, atomically — never one candidate at a time across the
        // whole function. Two structurally-shaped candidates that share the same loop body (e.g. a
        // copy loop's src-load and dst-store pointers) are mutually trusted by the whitelist below:
        // each one's audit treats the OTHER's reload/step/storeback as safe, on the assumption both end
        // up register-resident together. That assumption is only sound if it always holds. Committing
        // candidates one at a time from a single function-wide register pool breaks it the moment the
        // pool runs low: picture two sequential copy loops, each needing Hl+De for its src/dst pair —
        // the first loop's src and the SECOND loop's src both get admitted first (loads sorted first),
        // claiming Hl and De between them, leaving neither loop's dst pointer a register — so loop 1's
        // dst silently falls back to the ordinary path. But the ordinary path computes a dynamic gep by
        // materializing the address in HL (ComputeGepIntoHL/LoadPointerToHL) and the index in DE
        // (LoadIndexToDE) — unconditionally, on every use — which stomps the SAME Hl the loop's own src
        // pointer is still resident in, mid-loop-body, corrupting it before the loop's fused dereference
        // reads it again next iteration. (Confirmed by end-to-end repro: two sequential/branched/nested
        // stride-1 pointer-walk loops in one function corrupted memory — see
        // Sm83LoopCodegenTests.TwoSequentialCopyLoops_DistinctBuffers.)
        //
        // The fix: group candidates by their owning loop (Preheader uniquely identifies one — a block
        // can end in only one terminator, so it can be the unconditional-branch preheader of at most one
        // header) and audit/admit each group as a single unit. A group's whitelist is scoped to ONLY
        // that group's own reload/step/deref/storeback instructions (not the whole function's — a
        // sibling loop's candidates share no blocks with this one anyway, so widening the whitelist to
        // them bought nothing and only invited exactly this hazard). If the group doesn't fit the
        // register pairs still free at this point (bitwise, against everything assigned so far — Layer 0
        // residents and any earlier-admitted loop group), NONE of the group's candidates are admitted —
        // every one of them falls back to the ordinary path together, so there is no partial-admission
        // window for the ordinary path's HL/DE scratch use to corrupt a sibling that DID get a register.
        //
        // ponytail: a loop group's Hl/De claim is never released once assigned — it is held "for the
        // rest of the function" exactly like Layer 1's registers (see that region's header comment), so
        // a SECOND sequential copy loop always declines once the first one saturates the pool, even
        // though the two never run concurrently and could safely share the same physical registers.
        // Correct but leaves residency on the floor for any function with 2+ two-candidate loops.
        // Upgrade path (not yet needed by any measured hot loop): track each admitted group's live range
        // (its Body block set) and free its claimed pair(s) once no later-processed group's Body can
        // reach a point after this group's loop exits without passing back through it — i.e. once the
        // groups are provably sequential, not nested/overlapping (reuse Dominators/CollectLoopBody,
        // already computed above, rather than adding a new liveness pass).
        var pairPool = new[] { Sm83Register.Hl, Sm83Register.De };

        foreach (
            var loopGroup in candidates.GroupBy(
                c => c.Preheader,
                ReferenceEqualityComparer.Instance
            )
        )
        {
            // Loads first within the group so a load candidate keeps first claim on Hl (cheaper: one
            // opcode) when both shapes are present in the same loop — same preference as before.
            var group = loopGroup.OrderByDescending(c => c.IsLoad).ToList();
            if (group.Count > pairPool.Length)
                continue; // more pointers in this one loop than register pairs exist — never admitted

            var groupReloads = new HashSet<IrInstruction>(
                group.Select(c => (IrInstruction)c.Reload),
                ReferenceEqualityComparer.Instance
            );
            var groupSteps = new HashSet<IrInstruction>(
                group.Select(c => (IrInstruction)c.Step),
                ReferenceEqualityComparer.Instance
            );
            var groupDerefs = new HashSet<IrInstruction>(
                group.Select(c => c.Deref),
                ReferenceEqualityComparer.Instance
            );
            var groupStorebacks = new HashSet<IrInstruction>(
                group.Select(c => (IrInstruction)c.Storeback),
                ReferenceEqualityComparer.Instance
            );

            bool safe = true;
            foreach (var cand in group)
            foreach (var block in cand.Body)
            foreach (var instr in block.Instructions)
                if (
                    !IsPointerLoopSafeInstruction(
                        instr,
                        groupReloads,
                        groupDerefs,
                        groupSteps,
                        groupStorebacks
                    )
                )
                {
                    safe = false;
                    break;
                }
            if (!safe)
                continue;

            // Every candidate in the group must get a register, or none do (atomic admission — see the
            // region comment above). Collect enough distinct free pairs before committing any of them.
            var freeRegs = new List<Sm83Register>();
            foreach (var reg in pairPool)
                if (
                    assigned.Values.All(r => (r & reg) == 0)
                    && register.Values.All(r => (r & reg) == 0)
                )
                    freeRegs.Add(reg);
            if (freeRegs.Count < group.Count)
                continue;

            for (int i = 0; i < group.Count; i++)
            {
                var cand = group[i];
                var picked = freeRegs[i];

                register[cand.Reload] = picked;
                register[cand.Step] = picked;
                fusedSite[cand.Deref] = picked;

                var home = (AllocaInstruction)cand.Reload.Pointer; // guaranteed by the Phase 1 filter
                if (!preheaderSync.TryGetValue(cand.Preheader, out var syncs))
                    preheaderSync[cand.Preheader] = syncs = new List<PointerHomeSync>();
                syncs.Add(new PointerHomeSync(home, picked));
            }
        }

        return (register, preheaderSync, fusedSite);
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
                    if (colored.Contains(phi))
                        blockPhis.Add(phi); // only coloured phis have a graph node to interfere on
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
