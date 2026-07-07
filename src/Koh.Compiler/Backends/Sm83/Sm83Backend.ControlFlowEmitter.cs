using Koh.Compiler.Ir;
using Koh.Core.Binding;

namespace Koh.Compiler.Backends.Sm83;

public sealed partial class Sm83Backend
{
    /// <summary>Lowers ret/call/branch/switch/phi-copy control-flow instructions.</summary>
    internal sealed class ControlFlowEmitter : EmitBase
    {
        public ControlFlowEmitter(
            Emitter emitter,
            IrFunction fn,
            IReadOnlyDictionary<IrFunction, FunctionAllocation> allocations,
            IReadOnlyDictionary<IrGlobal, int> globals,
            IReadOnlySet<IrFunction> recursive,
            bool isEntry,
            int softStackBase,
            IReadOnlySet<IrFunction>? banked = null)
            : base(emitter, fn, allocations, globals, recursive, isEntry, softStackBase, banked) { }

        // ---- Control flow --------------------------------------------------

        public void EmitRet(RetInstruction r)
        {
            if (UsesMemoryReturn(_fn))
            {
                // Return via ReturnScratch (memory) so neither the recursive frame restore nor the far-call
                // thunk's bank restore can clobber it. A recursive function also restores its frame here.
                if (r.Value is not null)
                    CopyToScratch(r.Value, ReturnScratch, SizeOf(r.Value.Type));
                if (IsRecursive && _frameSize > 0)
                {
                    LdDE(_e, _frameBase); // LD DE, frameBase
                    _e.U8(0x06); _e.U8(_frameSize);                                // LD B, frameSize
                    _e.Jump(0xCD, _e.RoutineLabel("rt.popframe"));
                }
                _e.U8(0xC9);                                                   // RET (a banked fn is never a handler)
                return;
            }

            if (r.Value is not null)
            {
                switch (SizeOf(r.Value.Type))
                {
                    case 1:
                        LoadByteToA(r.Value, 0);
                        break;
                    case 2:
                        LoadByteToA(r.Value, 0);
                        _e.U8(0x6F);            // LD L, A
                        LoadByteToA(r.Value, 1);
                        _e.U8(0x67);            // LD H, A
                        break;
                    case 4:
                        // i32: low word in HL, high word in DE.
                        LoadByteToA(r.Value, 0); _e.U8(0x6F); // LD L, A
                        LoadByteToA(r.Value, 1); _e.U8(0x67); // LD H, A
                        LoadByteToA(r.Value, 2); _e.U8(0x5F); // LD E, A
                        LoadByteToA(r.Value, 3); _e.U8(0x57); // LD D, A
                        break;
                    case 8:
                    case 16:
                        // i64/i128 have no register room; return in the fixed ReturnScratch (little-endian).
                        CopyToScratch(r.Value, ReturnScratch, SizeOf(r.Value.Type));
                        break;
                    default:
                        throw new NotSupportedException(
                            $"SM83 backend can only return i8 (A), i16 (HL), i32 (DE:HL), or i64/i128 "
                            + $"(memory), not {r.Value.Type}.");
                }
            }

            if (_fn.InterruptVector is not null)
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
                case "ei": _e.U8(0xFB); break;
                case "di": _e.U8(0xF3); break;
                case "halt": _e.U8(0x76); _e.U8(0x00); break; // HALT + NOP (halt-bug guard)
                case "nop": _e.U8(0x00); break;
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
                    $"SM83 backend cannot yet call external function '@{callee.Name}'.");

            bool calleeRecursive = _recursive.Contains(callee);
            bool calleeBanked = _banked.Contains(callee);

            if (calleeRecursive)
            {
                // A recursive callee shares its static frame across invocations, so it takes arguments
                // through the ArgScratch staging area; writing straight into its parameter slots would
                // corrupt an ancestor.
                int off = 0;
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    int n = SizeOf(callee.Parameters[i].Type);
                    CopyToScratch(call.Arguments[i], ArgScratch + off, n);
                    off += n;
                }
            }
            else
            {
                var calleeAllocation = _allocations[callee];
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    var param = callee.Parameters[i];
                    CopyToScratch(call.Arguments[i], calleeAllocation.Slot[param], SizeOf(param.Type));
                }
            }

            // A banked callee is reached through its ROM0 far-call thunk (which maps the callee's bank);
            // an unbanked one is called directly at its entry.
            _e.Jump(0xCD, calleeBanked ? _e.ThunkLabel(callee) : _e.FunctionLabel(callee));

            if (call.Type.Kind == IrTypeKind.Void)
                return;

            int dst = _slot[call];

            // A recursive or banked callee returns through ReturnScratch (memory); read it back.
            if (calleeRecursive || calleeBanked)
            {
                CopyFromScratch(ReturnScratch, dst, SizeOf(call.Type));
                return;
            }

            switch (SizeOf(call.Type))
            {
                case 1:
                    StoreAToAddr(dst);                    // result in A
                    break;
                case 2:
                    _e.U8(0x7D); StoreAToAddr(dst);       // LD A, L ; store low
                    _e.U8(0x7C); StoreAToAddr(dst + 1);   // LD A, H ; store high
                    break;
                case 4:
                    _e.U8(0x7D); StoreAToAddr(dst);       // LD A, L
                    _e.U8(0x7C); StoreAToAddr(dst + 1);   // LD A, H
                    _e.U8(0x7B); StoreAToAddr(dst + 2);   // LD A, E
                    _e.U8(0x7A); StoreAToAddr(dst + 3);   // LD A, D
                    break;
                case 8:
                case 16:
                    // i64/i128 come back in ReturnScratch.
                    CopyFromScratch(ReturnScratch, dst, SizeOf(call.Type));
                    break;
                default:
                    throw new NotSupportedException(
                        $"SM83 backend can only capture i8/i16/i32/i64/i128 return values, not {call.Type}.");
            }
        }

        public void EmitBr(IrBasicBlock source, BrInstruction br)
        {
            EmitPhiCopies(source, br.Target);
            _e.Jump(0xC3, _e.BlockLabel(br.Target)); // JP a16
        }

        public void EmitCondBr(IrBasicBlock source, CondBrInstruction cb)
        {
            LoadByteToA(cb.Condition, 0);
            _e.U8(0xA7);                                 // AND A -> Z set iff false
            var trueEdge = new Label();
            _e.Jump(0xC2, trueEdge);                     // JP NZ, <true edge>

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
                _e.Jump(0xCA, edge);                   // JP Z, <edge>
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
                LoadByteToA(value, k);
                _e.U8(0xEE); _e.U8(Sm83Ops.ByteOf(caseConst, k));  // XOR d8
                if (k == 0)
                {
                    _e.U8(0x4F);                            // LD C, A
                }
                else
                {
                    _e.U8(0xB1);                            // OR C
                    _e.U8(0x4F);                            // LD C, A
                }
            }
        }

        /// <summary>
        /// Realize the phi nodes of <paramref name="target"/> for the edge from
        /// <paramref name="source"/> as a parallel copy: all reads observe the values that held
        /// on entry to the edge. Copies whose destination is still needed as a source are deferred,
        /// and cycles (e.g. a swap <c>a,b = b,a</c>) are broken by staging one value through a temp.
        /// </summary>
        private void EmitPhiCopies(IrBasicBlock source, IrBasicBlock target)
        {
            var pending = new List<PhiCopy>();
            foreach (var instr in target.Instructions)
            {
                if (instr is not PhiInstruction phi)
                    break; // phis lead the block
                pending.Add(new PhiCopy(phi, _slot[phi], SizeOf(phi.Type), FindIncoming(phi, source)));
            }
            if (pending.Count == 0)
                return;

            int temp = _phiTempBase;

            while (pending.Count > 0)
            {
                int before = pending.Count;

                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var c = pending[i];
                    // Emitting c writes its destination bytes; defer if that would clobber a slot
                    // another pending copy still has to read. This is by *slot*, not SSA identity:
                    // register coalescing can put an unrelated source in a phi-destination's slot.
                    bool clobbersPendingSource = pending.Any(o => o != c && SourceOverlaps(o, c.DestSlot, c.N));
                    if (!clobbersPendingSource)
                    {
                        EmitMove(c);
                        pending.RemoveAt(i);
                    }
                }

                if (pending.Count == before)
                {
                    // Every remaining copy is part of a cycle: stage one destination through a temp,
                    // redirect readers of that slot there, then let the next pass drain the chain.
                    var c = pending[0];
                    int tempAddr = temp;
                    temp += c.N;
                    for (int k = 0; k < c.N; k++)
                    {
                        LoadAFromAddr(c.DestSlot + k);
                        StoreAToAddr(tempAddr + k);
                    }
                    foreach (var o in pending)
                        if (o != c && SourceSlot(o, out int srcAddr)
                            && srcAddr < c.DestSlot + c.N && c.DestSlot < srcAddr + o.N)
                        {
                            // Read from the staged copy at the source's offset within the range, so a
                            // source coalesced at a non-zero position inside the slot still reads right.
                            o.Src = null;
                            o.TempSrc = tempAddr + (srcAddr - c.DestSlot);
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
            return TryStaticAddr(o.Src, out srcAddr) || _slot.TryGetValue(o.Src, out srcAddr);
        }

        private void EmitMove(PhiCopy c)
        {
            for (int k = 0; k < c.N; k++)
            {
                if (c.TempSrc >= 0)
                    LoadAFromAddr(c.TempSrc + k);
                else
                    LoadByteToA(c.Src!, k);
                StoreAToAddr(c.DestSlot + k);
            }
        }

        /// <summary>One pending phi realization: write <see cref="N"/> bytes from a source into a slot.</summary>
        private sealed class PhiCopy
        {
            public IrValue DestPhi { get; }
            public int DestSlot { get; }
            public int N { get; }
            public IrValue? Src { get; set; }   // value source; null once redirected to a temp
            public int TempSrc { get; set; } = -1;

            public PhiCopy(IrValue destPhi, int destSlot, int n, IrValue src)
            {
                DestPhi = destPhi;
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
