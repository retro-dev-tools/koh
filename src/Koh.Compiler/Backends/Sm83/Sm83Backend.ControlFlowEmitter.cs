using Koh.Compiler.Ir;
using Koh.Core.Binding;

namespace Koh.Compiler.Backends.Sm83;

public sealed partial class Sm83Backend
{
    /// <summary>Lowers ret/call/branch/switch/phi-copy control-flow instructions.</summary>
    internal sealed class ControlFlowEmitter
    {
        private readonly EmitContext _ctx;
        private readonly Emitter _e;

        public ControlFlowEmitter(EmitContext ctx)
        {
            _ctx = ctx;
            _e = ctx.E;
        }

        // ---- Control flow --------------------------------------------------

        public void EmitRet(RetInstruction r)
        {
            if (_ctx.UsesMemoryReturn(_ctx.Fn))
            {
                // Return via ReturnScratch (memory) so neither the recursive frame restore nor the far-call
                // thunk's bank restore can clobber it. A recursive function also restores its frame here.
                if (r.Value is not null)
                    _ctx.CopyToScratch(r.Value, ReturnScratch, SizeOf(r.Value.Type));
                if (_ctx.IsRecursive && _ctx.FrameSize > 0)
                {
                    LdDE(_e, _ctx.FrameBase); // LD DE, frameBase
                    _e.U8(0x06);
                    _e.U8(_ctx.FrameSize); // LD B, frameSize
                    _e.Jump(0xCD, _e.RoutineLabel("rt.popframe"));
                }
                _e.U8(0xC9); // RET (a banked fn is never a handler)
                return;
            }

            if (r.Value is not null)
            {
                switch (SizeOf(r.Value.Type))
                {
                    case 1:
                        _ctx.LoadByteToA(r.Value, 0);
                        break;
                    case 2:
                        _ctx.LoadByteToA(r.Value, 0);
                        _e.U8(0x6F); // LD L, A
                        _ctx.LoadByteToA(r.Value, 1);
                        _e.U8(0x67); // LD H, A
                        break;
                    case 4:
                        // i32: low word in HL, high word in DE.
                        _ctx.LoadByteToA(r.Value, 0);
                        _e.U8(0x6F); // LD L, A
                        _ctx.LoadByteToA(r.Value, 1);
                        _e.U8(0x67); // LD H, A
                        _ctx.LoadByteToA(r.Value, 2);
                        _e.U8(0x5F); // LD E, A
                        _ctx.LoadByteToA(r.Value, 3);
                        _e.U8(0x57); // LD D, A
                        break;
                    case 8:
                    case 16:
                        // i64/i128 have no register room; return in the fixed ReturnScratch (little-endian).
                        _ctx.CopyToScratch(r.Value, ReturnScratch, SizeOf(r.Value.Type));
                        break;
                    default:
                        throw new NotSupportedException(
                            $"SM83 backend can only return i8 (A), i16 (HL), i32 (DE:HL), or i64/i128 "
                                + $"(memory), not {r.Value.Type}."
                        );
                }
            }

            if (_ctx.Fn.InterruptVector is not null)
            {
                _e.U8(0xE1); // POP HL
                _e.U8(0xD1); // POP DE
                _e.U8(0xC1); // POP BC
                _e.U8(0xF1); // POP AF
                _e.U8(0xD9); // RETI
            }
            else
            {
                _e.U8(0xC9); // RET
            }
        }

        public void EmitIntrinsic(IntrinsicInstruction instr)
        {
            switch (instr.Intrinsic)
            {
                case "ei":
                    _e.U8(0xFB);
                    break;
                case "di":
                    _e.U8(0xF3);
                    break;
                case "halt":
                    _e.U8(0x76);
                    _e.U8(0x00);
                    break; // HALT + NOP (halt-bug guard)
                case "stop":
                    _e.U8(0x10);
                    _e.U8(0x00);
                    break;
                case "nop":
                    _e.U8(0x00);
                    break;
                default:
                    throw new NotSupportedException($"unknown intrinsic '{instr.Intrinsic}'.");
            }
        }

        /// <summary>
        /// Lower a direct call: write each argument into the callee's parameter slots (its frame
        /// is disjoint), <c>CALL</c> the callee's entry, then capture the return (<c>A</c> for i8,
        /// <c>HL</c> for i16) into this call's slot.
        /// </summary>
        public void EmitCall(CallInstruction call)
        {
            var callee = call.Callee;
            if (callee.IsExternal || callee.EntryBlock is null)
                throw new NotSupportedException(
                    $"SM83 backend cannot yet call external function '@{callee.Name}'."
                );

            bool calleeRecursive = _ctx.Recursive.Contains(callee);
            bool calleeBanked = _ctx.Banked.Contains(callee);

            if (calleeRecursive)
            {
                // A recursive callee shares its static frame across invocations, so it takes arguments
                // through the ArgScratch staging area; writing straight into its parameter slots would
                // corrupt an ancestor.
                int off = 0;
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    int n = SizeOf(callee.Parameters[i].Type);
                    _ctx.CopyToScratch(call.Arguments[i], ArgScratch + off, n);
                    off += n;
                }
            }
            else
            {
                var calleeAllocation = _ctx.Allocations[callee];
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    var param = callee.Parameters[i];
                    int n = SizeOf(param.Type);
                    // A parameter received in a register (the register calling convention) is placed there
                    // directly; otherwise it is written to the callee's WRAM parameter slot. Arguments are
                    // never caller-residents (they feed this non-gentle call), so loading one only touches
                    // A — placing several in distinct registers cannot clobber one another.
                    if (calleeAllocation.Register.TryGetValue(param, out var reg))
                        _ctx.LoadValueIntoRegister(call.Arguments[i], reg, n);
                    else
                        _ctx.CopyToScratch(call.Arguments[i], calleeAllocation.Slot[param], n);
                }
            }

            // A banked callee is reached through its ROM0 far-call thunk (which maps the callee's bank);
            // an unbanked one is called directly at its entry.
            _e.Jump(0xCD, calleeBanked ? _e.ThunkLabel(callee) : _e.FunctionLabel(callee));

            if (call.Type.Kind == IrTypeKind.Void)
                return;

            int dst = _ctx.Slot[call];

            // A recursive or banked callee returns through ReturnScratch (memory); read it back.
            if (_ctx.UsesMemoryReturn(callee))
            {
                _ctx.CopyFromScratch(ReturnScratch, dst, SizeOf(call.Type));
                return;
            }

            switch (SizeOf(call.Type))
            {
                case 1:
                    _ctx.StoreAToAddr(dst); // result in A
                    break;
                case 2:
                    _ctx.StoreRegPair(dst, 2, hi: 0x7C, lo: 0x7D); // HL -> slot
                    break;
                case 4:
                    _ctx.StoreRegPair(dst, 2, hi: 0x7C, lo: 0x7D); // HL -> low word
                    _ctx.StoreRegPair(dst + 2, 2, hi: 0x7A, lo: 0x7B); // DE -> high word
                    break;
                case 8:
                case 16:
                    // i64/i128 come back in ReturnScratch.
                    _ctx.CopyFromScratch(ReturnScratch, dst, SizeOf(call.Type));
                    break;
                default:
                    throw new NotSupportedException(
                        $"SM83 backend can only capture i8/i16/i32/i64/i128 return values, not {call.Type}."
                    );
            }
        }

        public void EmitBr(IrBasicBlock source, BrInstruction br)
        {
            EmitPhiCopies(source, br.Target);
            _e.Jump(0xC3, _e.BlockLabel(br.Target)); // JP a16
        }

        public void EmitCondBr(IrBasicBlock source, CondBrInstruction cb)
        {
            _ctx.LoadByteToA(cb.Condition, 0);
            _e.U8(0xA7); // AND A -> Z set iff false
            var trueEdge = new Label();
            _e.Jump(0xC2, trueEdge); // JP NZ, <true edge>

            // False edge (fall-through): copy phis then jump.
            EmitPhiCopies(source, cb.IfFalse);
            _e.Jump(0xC3, _e.BlockLabel(cb.IfFalse));

            // True edge.
            _e.Place(trueEdge);
            EmitPhiCopies(source, cb.IfTrue);
            _e.Jump(0xC3, _e.BlockLabel(cb.IfTrue));
        }

        /// <summary>Lower a switch as a chain of equality tests, each branching to a split edge.</summary>
        public void EmitSwitch(IrBasicBlock source, SwitchInstruction sw)
        {
            int n = SizeOf(sw.Value.Type);
            var edges = new List<(Label Edge, IrBasicBlock Target)>();

            foreach (var (caseConst, target) in sw.Cases)
            {
                EmitEqualityZ(sw.Value, caseConst, n); // Z set iff value == caseConst
                var edge = new Label();
                _e.Jump(0xCA, edge); // JP Z, <edge>
                edges.Add((edge, target));
            }

            // No case matched: fall through to the default edge.
            EmitPhiCopies(source, sw.Default);
            _e.Jump(0xC3, _e.BlockLabel(sw.Default));

            foreach (var (edge, target) in edges)
            {
                _e.Place(edge);
                EmitPhiCopies(source, target);
                _e.Jump(0xC3, _e.BlockLabel(target));
            }
        }

        /// <summary>Leave Z set iff <paramref name="value"/> equals <paramref name="caseConst"/>.</summary>
        private void EmitEqualityZ(IrValue value, IrConstInt caseConst, int n)
        {
            for (int k = 0; k < n; k++)
            {
                _ctx.LoadByteToA(value, k);
                _e.U8(0xEE);
                _e.U8(Sm83Ops.ByteOf(caseConst, k)); // XOR d8
                if (k == 0)
                {
                    _e.U8(0x4F); // LD C, A
                }
                else
                {
                    _e.U8(0xB1); // OR C
                    _e.U8(0x4F); // LD C, A
                }
            }
        }

        /// <summary>
        /// Realize the phi nodes of <paramref name="target"/> for the edge from
        /// <paramref name="source"/> as a parallel copy: all reads observe the values that held
        /// on entry to the edge. Copies whose destination is still needed as a source are deferred,
        /// and cycles (e.g. a swap <c>a,b = b,a</c>) are broken by staging one value through a temp.
        /// </summary>
        /// <remarks>
        /// Already width- and residency-agnostic, including for a width-2 (i16/pointer) resident phi —
        /// no change needed to widen Layer 1's induction residency (loop-residency-generalization spec,
        /// step 1). <c>PhiCopy.N</c> is <c>SizeOf(phi.Type)</c>, which for a pointer resolves through
        /// <c>IrType.SizeInBytes</c> to <c>DataLayout.Sm83.PointerSize</c> (2), never the bit-derived
        /// <c>(Bits + 7) / 8</c> that would silently size a pointer at 0 (see CLAUDE.md's pointer-sizing
        /// invariant). <c>EmitMove</c> reads each byte via <c>LoadByteToA</c>, which for a
        /// register-resident source already selects the low/high half of an <c>Hl</c>/<c>De</c> pair at
        /// <c>k</c> = 0/1 (<c>ResidentToAOpcode</c>) exactly as it does a single byte register — so a
        /// dual-placement resident phi (phi keeps its <c>Slot</c> entry, its back-edge value is
        /// register-only, per Layer 1's scheme) collapses each byte of the edge copy from a
        /// slot-to-slot reload+store into a register-to-slot store, for any width. <c>SourceSlot</c>
        /// already treats a register-only source (no <c>Slot</c> entry) as reading no live slot, so it
        /// never blocks another pending copy or gets pulled into the cycle-breaking path. What is
        /// missing is purely on the selection side (step 2): no residency selector admits a width-2
        /// loop-carried phi yet, so this path is unreached until then.
        /// </remarks>
        private void EmitPhiCopies(IrBasicBlock source, IrBasicBlock target)
        {
            var pending = new List<PhiCopy>();
            foreach (var instr in target.Instructions)
            {
                if (instr is not PhiInstruction phi)
                    break; // phis lead the block
                var incoming = FindIncoming(phi, source);
                int destSlot = _ctx.Slot[phi];

                // Identity copy: this edge's incoming value already lives at the phi's own slot
                // address — either it IS the phi itself (a pass-through loop-carried value that this
                // block's own back edge never changes, e.g. Mem.Copy's outer block/remainder counters
                // ambient across its shared inner loop — see Sm83FunctionAllocation's "Loop-induction
                // register residency" region), or some other SSA value the allocator's interference
                // colouring happened to coalesce onto the exact same address. Either way, writing a
                // slot with the bytes already sitting there changes no memory — a same-address,
                // same-width move is a no-op by construction (phi/incoming types match, so the byte
                // ranges are identical) — so skip the WRAM read+store round trip entirely rather than
                // paying for it every time this edge runs (once per outer iteration here; once per
                // back edge in a tighter loop). Never touches a register-resident source (Layer 1's
                // dual-placement phis, whose incoming never resolves a Slot address here at all) — that
                // back-edge write stays exactly as the module's design notes require.
                if (TryResolveSlotAddress(incoming, out int srcSlot) && srcSlot == destSlot)
                    continue;

                pending.Add(new PhiCopy(destSlot, SizeOf(phi.Type), incoming));
            }
            if (pending.Count == 0)
                return;

            int temp = _ctx.PhiTempBase;

            while (pending.Count > 0)
            {
                int before = pending.Count;

                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var c = pending[i];
                    // Emitting c writes its destination bytes; defer if that would clobber a slot
                    // another pending copy still has to read. This is by *slot*, not SSA identity:
                    // register coalescing can put an unrelated source in a phi-destination's slot.
                    bool clobbersPendingSource = pending.Any(o =>
                        o != c && SourceOverlaps(o, c.DestSlot, c.N)
                    );
                    if (!clobbersPendingSource)
                    {
                        EmitMove(c);
                        pending.RemoveAt(i);
                    }
                }

                if (pending.Count == before)
                {
                    // Every remaining copy is part of a cycle: stage c's destination footprint through a
                    // temp, redirect readers of that slot there, then let the next pass drain the chain.
                    //
                    // The staged range must be the *union* of c's destination and every blocked reader's
                    // source, not just c's own width: a narrower copy can still block a wider one whose
                    // source starts at the same address but extends past c's destination (e.g. a 4-byte
                    // i32 phi and an 8-byte i64 phi swapping slots — the i64 reader needs bytes the i32
                    // copy's own N never covers). Staging only `c.N` bytes there would redirect the wider
                    // reader onto a temp region whose tail was never written, reading uninitialized WRAM.
                    var c = pending[0];
                    int stageStart = c.DestSlot;
                    int stageEnd = c.DestSlot + c.N;
                    foreach (var o in pending)
                        if (
                            o != c
                            && SourceSlot(o, out int osrcAddr)
                            && osrcAddr < c.DestSlot + c.N
                            && c.DestSlot < osrcAddr + o.N
                        )
                        {
                            stageStart = Math.Min(stageStart, osrcAddr);
                            stageEnd = Math.Max(stageEnd, osrcAddr + o.N);
                        }
                    int tempAddr = temp;
                    int stageLen = stageEnd - stageStart;
                    temp += stageLen;
                    for (int k = 0; k < stageLen; k++)
                    {
                        _ctx.LoadAFromAddr(stageStart + k);
                        _ctx.StoreAToAddr(tempAddr + k);
                    }
                    foreach (var o in pending)
                        if (
                            o != c
                            && SourceSlot(o, out int srcAddr)
                            && srcAddr < c.DestSlot + c.N
                            && c.DestSlot < srcAddr + o.N
                        )
                        {
                            // Read from the staged copy at the source's offset within the (possibly
                            // wider-than-c) staged range, so a source coalesced at a non-zero position
                            // inside the slot still reads right.
                            o.Src = null;
                            o.TempSrc = tempAddr + (srcAddr - stageStart);
                        }
                }
            }
        }

        /// <summary>Whether pending copy <paramref name="o"/> reads a slot range overlapping
        /// <c>[start, start + n)</c>. Constants and temp-staged sources read no live slot.</summary>
        private bool SourceOverlaps(PhiCopy o, int start, int n) =>
            SourceSlot(o, out int srcAddr) && srcAddr < start + n && start < srcAddr + o.N;

        /// <summary>The WRAM address a pending copy reads from, if it reads a slot (not a constant or
        /// a value already staged into a temp).</summary>
        private bool SourceSlot(PhiCopy o, out int srcAddr)
        {
            srcAddr = 0;
            if (o.TempSrc >= 0 || o.Src is null or IrConstInt)
                return false;
            return _ctx.TryStaticAddr(o.Src, out srcAddr)
                || _ctx.Slot.TryGetValue(o.Src, out srcAddr);
        }

        /// <summary>The WRAM address <paramref name="value"/> resolves to (a static address or a
        /// coloured slot), used only by <see cref="EmitPhiCopies"/>'s identity-copy check above — a
        /// constant never aliases a slot address, and a register-resident value has none, so both
        /// correctly report no slot.</summary>
        private bool TryResolveSlotAddress(IrValue value, out int addr)
        {
            addr = 0;
            if (value is IrConstInt)
                return false;
            return _ctx.TryStaticAddr(value, out addr) || _ctx.Slot.TryGetValue(value, out addr);
        }

        private void EmitMove(PhiCopy c)
        {
            for (int k = 0; k < c.N; k++)
            {
                if (c.TempSrc >= 0)
                    _ctx.LoadAFromAddr(c.TempSrc + k);
                else
                    _ctx.LoadByteToA(c.Src!, k);
                _ctx.StoreAToAddr(c.DestSlot + k);
            }
        }

        /// <summary>One pending phi realization: write <see cref="N"/> bytes from a source into a slot.</summary>
        private sealed class PhiCopy
        {
            public int DestSlot { get; }
            public int N { get; }
            public IrValue? Src { get; set; } // value source; null once redirected to a temp
            public int TempSrc { get; set; } = -1;

            public PhiCopy(int destSlot, int n, IrValue src)
            {
                DestSlot = destSlot;
                N = n;
                Src = src;
            }
        }

        private static IrValue FindIncoming(PhiInstruction phi, IrBasicBlock source)
        {
            foreach (var (value, block) in phi.Incomings)
                if (ReferenceEquals(block, source))
                    return value;
            throw new NotSupportedException("phi has no incoming for a predecessor edge.");
        }
    }
}
