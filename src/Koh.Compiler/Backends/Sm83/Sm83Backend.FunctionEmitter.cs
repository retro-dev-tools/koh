using Koh.Compiler.Ir;
using Koh.Core.Binding;

namespace Koh.Compiler.Backends.Sm83;

public sealed partial class Sm83Backend
{
    /// <summary>Lowers one function into the shared emitter using the static-allocation model.</summary>
    private sealed class FunctionEmitter
    {
        private readonly Emitter _e;
        private readonly IrFunction _fn;
        private readonly IReadOnlyDictionary<IrFunction, FunctionAllocation> _allocations;
        private readonly IReadOnlyDictionary<IrGlobal, int> _globals;
        private readonly Dictionary<IrValue, int> _slot;
        private readonly Dictionary<IrValue, int> _staticAddr;
        private readonly int _phiTempBase;
        private readonly IReadOnlySet<IrFunction> _recursive;
        private readonly IReadOnlySet<IrFunction> _banked;
        private readonly bool _isEntry;
        private readonly int _softStackBase;
        private readonly int _frameBase;
        private readonly int _frameSize;

        public FunctionEmitter(
            Emitter emitter,
            IrFunction fn,
            IReadOnlyDictionary<IrFunction, FunctionAllocation> allocations,
            IReadOnlyDictionary<IrGlobal, int> globals,
            IReadOnlySet<IrFunction> recursive,
            bool isEntry,
            int softStackBase,
            IReadOnlySet<IrFunction>? banked = null)
        {
            _e = emitter;
            _fn = fn;
            _allocations = allocations;
            _globals = globals;
            var allocation = allocations[fn];
            _slot = allocation.Slot;
            _staticAddr = allocation.StaticAddr;
            _phiTempBase = allocation.PhiTempBase;
            _recursive = recursive;
            _banked = banked ?? System.Collections.Immutable.ImmutableHashSet<IrFunction>.Empty;
            _isEntry = isEntry;
            _softStackBase = softStackBase;
            _frameBase = allocation.FrameBase;
            _frameSize = allocation.FrameEnd - allocation.FrameBase;
        }

        private bool IsRecursive => _recursive.Contains(_fn);

        /// <summary>Whether this function returns its value through <see cref="ReturnScratch"/> rather
        /// than registers: recursive functions (so the frame restore cannot clobber it) and banked
        /// functions (so the far-call thunk's bank restore cannot clobber it).</summary>
        private bool UsesMemoryReturn(IrFunction fn) => _recursive.Contains(fn) || _banked.Contains(fn);

        /// <summary>Total bytes of a function's parameters (contiguous at its frame base).</summary>
        private static int ParamBytes(IrFunction fn)
        {
            int bytes = 0;
            foreach (var p in fn.Parameters)
                bytes += SizeOf(p.Type);
            return bytes;
        }

        /// <summary>A compile-time-known address: an alloca/constant-gep, a global's address, or a
        /// constant-address pointer (e.g. <c>(byte*)0xFF40</c> for direct MMIO).</summary>
        private bool TryStaticAddr(IrValue value, out int addr)
        {
            if (_staticAddr.TryGetValue(value, out addr))
                return true;
            if (value is IrGlobalRef g && _globals.TryGetValue(g.Global, out addr))
                return true;
            if (value is IrConstInt c && value.Type.Kind == IrTypeKind.Pointer)
            {
                addr = (int)c.Value;
                return true;
            }
            addr = 0;
            return false;
        }

        public void Compile()
        {
            // The CALL target is here, before any prologue (the entry block label follows the prologue).
            _e.Place(_e.FunctionLabel(_fn));

            // Interrupt handlers must preserve everything they touch; push at entry, pop before RETI.
            if (_fn.InterruptVector is not null)
            {
                _e.U8(0xF5); // PUSH AF
                _e.U8(0xC5); // PUSH BC
                _e.U8(0xD5); // PUSH DE
                _e.U8(0xE5); // PUSH HL
            }

            if (_isEntry && _recursive.Count > 0)
            {
                // Move the hardware CALL stack into WRAM (it defaults to the tiny HRAM window, where deep
                // recursion overflows into the I/O registers and crashes). Growing down from just below
                // ArgScratch gives it the whole arena above the static frames.
                _e.U8(0x31); _e.U16(HwStackTop);      // LD SP, HwStackTop
                // Initialize the software-stack pointer once at boot (only needed when some function
                // recurses and therefore saves its frame there).
                LdHL(_e, _softStackBase); // LD HL, softStackBase
                _e.U8(0x7D); _e.StoreA(SoftSp);       // LD A, L ; LD (SoftSp), A
                _e.U8(0x7C); _e.StoreA(SoftSp + 1);   // LD A, H ; LD (SoftSp+1), A
            }

            if (IsRecursive)
            {
                if (_frameSize > 255)
                    throw new Sm83LimitException(
                        $"recursive function '{_fn.Name}' frame is {_frameSize} bytes; the software-stack "
                        + "save supports up to 255.");
                // Save the caller's copy of the shared static frame, then install this call's arguments
                // (staged in ArgScratch) into the parameter slots at the frame base. A zero-byte frame
                // (no params/locals — recursion driven through globals) has nothing to save; skip it, as
                // rt.pushframe with a count of 0 would `dec b` to 0xFF and copy 256 bytes.
                if (_frameSize > 0)
                {
                    LdDE(_e, _frameBase); // LD DE, frameBase
                    _e.U8(0x06); _e.U8(_frameSize);                                // LD B, frameSize
                    _e.Jump(0xCD, _e.RoutineLabel("rt.pushframe"));
                    int paramBytes = ParamBytes(_fn);
                    for (int k = 0; k < paramBytes; k++)
                    {
                        _e.LoadA(ArgScratch + k);
                        _e.StoreA(_frameBase + k);
                    }
                }
            }

            foreach (var block in _fn.Blocks)
            {
                _e.Place(_e.BlockLabel(block));
                foreach (var instr in block.Instructions)
                {
                    int start = _e.Code.Count;
                    EmitInstruction(block, instr);
                    if (instr.Source is { } src && _e.Code.Count > start)
                        _e.AddLineRange(start, _e.Code.Count - start, src.File, src.Line);
                }
            }
        }

        private void EmitInstruction(IrBasicBlock block, IrInstruction instr)
        {
            switch (instr)
            {
                case BinaryInstruction b: EmitBinary(b); break;
                case CompareInstruction c: EmitCompare(c); break;
                case ConvInstruction cv: EmitConv(cv); break;
                case LoadInstruction l: EmitLoad(l); break;
                case StoreInstruction s: EmitStore(s); break;
                case AllocaInstruction: break;               // storage pre-assigned
                case GetElementPtrInstruction g:
                    if (_slot.ContainsKey(g))                // dynamic: compute the pointer at runtime
                        EmitGep(g);
                    break;                                   // static: address pre-assigned
                case PhiInstruction: break;                  // realized by predecessor edge copies
                case RetInstruction r: EmitRet(r); break;
                case BrInstruction br: EmitBr(block, br); break;
                case CondBrInstruction cb: EmitCondBr(block, cb); break;
                case SwitchInstruction sw: EmitSwitch(block, sw); break;
                case CallInstruction call: EmitCall(call); break;
                case IntrinsicInstruction intr: EmitIntrinsic(intr); break;
                default:
                    throw new NotSupportedException(
                        $"MVP SM83 backend does not support '{instr.Mnemonic}' (in '@{_fn.Name}').");
            }
        }

        // ---- Arithmetic ----------------------------------------------------

        private void EmitBinary(BinaryInstruction b)
        {
            if (b.Op is IrBinaryOp.Shl or IrBinaryOp.LShr or IrBinaryOp.AShr)
            {
                EmitShift(b);
                return;
            }

            if (b.Op is IrBinaryOp.Mul or IrBinaryOp.UDiv or IrBinaryOp.SDiv
                or IrBinaryOp.URem or IrBinaryOp.SRem)
            {
                EmitMulDivRem(b);
                return;
            }

            int n = SizeOf(b.Type);
            int dst = _slot[b];
            bool rightConst = b.Right is IrConstInt;

            for (int k = 0; k < n; k++)
            {
                if (rightConst)
                {
                    LoadByteToA(b.Left, k);
                    _e.U8(AluImmOpcode(b.Op, k));
                    _e.U8(ByteOf(b.Right, k));
                }
                else
                {
                    LoadByteToB(b.Right, k);
                    LoadByteToA(b.Left, k);
                    _e.U8(AluRegOpcode(b.Op, k));
                }
                StoreAToAddr(dst + k);
            }
        }

        /// <summary>
        /// Lower a shift. The value is shifted in <c>E</c> (i8) or <c>D:E</c> (i16), one bit per
        /// step: constant amounts are unrolled; a variable amount loops with the count in <c>B</c>.
        /// </summary>
        private void EmitShift(BinaryInstruction b)
        {
            int n = SizeOf(b.Type);
            if (n > 2)
            {
                EmitWideShift(b, n);
                return;
            }
            int dst = _slot[b];

            if (b.Right is IrConstInt amount)
            {
                LoadWorking(b.Left, n);
                int steps = Math.Min((int)amount.Value, n * 8);
                for (int s = 0; s < steps; s++)
                    ShiftWorkingOnce(b.Op, n);
                StoreWorking(dst, n);
                return;
            }

            // Variable amount. The count shares the value's type, so it is n bytes wide; loading only
            // its low byte would shift by a truncated amount. The loop shifts one bit per step and
            // reaches a fixed point at n*8 bits (0 for Shl/LShr, sign fill for AShr), so clamp the
            // count to n*8: a count whose high byte is set, or whose value meets/exceeds the width,
            // saturates to n*8 rather than looping by its truncated low byte.
            int width = n * 8;
            LoadByteToA(b.Right, 0);
            _e.U8(0x47);                 // LD B, A  (tentative count = low byte)
            var saturate = new Label();
            var counted = new Label();
            if (n == 2)
            {
                LoadByteToA(b.Right, 1);
                _e.U8(0xB7);                     // OR A            (Z iff high byte == 0)
                _e.Jump(0xC2, saturate);         // JP NZ, saturate (high bits set => count >= width)
            }
            _e.U8(0x78);                         // LD A, B
            _e.U8(0xFE); _e.U8((byte)width);     // CP width        (carry iff count < width)
            _e.Jump(0xDA, counted);              // JP C, counted   (count < width => use as-is)
            _e.Place(saturate);
            _e.U8(0x06); _e.U8((byte)width);     // LD B, width     (saturate)
            _e.Place(counted);
            LoadWorking(b.Left, n);
            var loop = new Label();
            var done = new Label();
            _e.Place(loop);
            _e.U8(0x78); _e.U8(0xA7);    // LD A, B ; AND A  (Z iff count == 0)
            _e.Jump(0xCA, done);         // JP Z, done
            ShiftWorkingOnce(b.Op, n);
            _e.U8(0x05);                 // DEC B
            _e.Jump(0xC3, loop);         // JP loop
            _e.Place(done);
            StoreWorking(dst, n);
        }

        /// <summary>Lower a 32-/64-bit multiply/divide/remainder via the generic width-N runtime routine:
        /// copy both operands into scratch, call, and copy the result back. Operands are fully read before
        /// the destination is written, so a result slot overlapping an operand is harmless here.</summary>
        private void EmitWideMulDivRem(BinaryInstruction b, int n)
        {
            int dst = _slot[b];
            CopyToScratch(b.Left, RtOpA, n);
            CopyToScratch(b.Right, RtOpB, n);
            _e.U8(0x3E); _e.U8(n); StoreAToAddr(RtN);       // LD A,n ; RtN = n
            string routine = b.Op switch
            {
                IrBinaryOp.Mul => "mul_wide",
                IrBinaryOp.UDiv or IrBinaryOp.URem => "udivmod_wide",
                _ => "sdivmod_wide",
            };
            _e.Jump(0xCD, _e.RoutineLabel(routine));
            // Product and remainder come back in RtAcc; quotient in RtOpA.
            int result = b.Op is IrBinaryOp.Mul or IrBinaryOp.URem or IrBinaryOp.SRem ? RtAcc : RtOpA;
            CopyFromScratch(result, dst, n);
        }

        /// <summary>Lower a 32-/64-bit shift: copy the subject into scratch, store the clamped count, call
        /// the width-N shift routine, and copy the result back. Mirrors the 16-bit count clamp.</summary>
        private void EmitWideShift(BinaryInstruction b, int n)
        {
            int dst = _slot[b];
            int width = n * 8;
            CopyToScratch(b.Left, RtOpA, n);
            _e.U8(0x3E); _e.U8(n); StoreAToAddr(RtN);       // LD A,n ; RtN = n

            if (b.Right is IrConstInt amount)
            {
                int steps = Math.Min((int)amount.Value, width);
                _e.U8(0x3E); _e.U8((byte)steps); StoreAToAddr(RtBits);
            }
            else
            {
                // Clamp the runtime count to the width: accumulate the high bytes in C; if any are set the
                // count meets/exceeds the width and saturates, else compare the low byte against the width.
                LoadByteToA(b.Right, 0);
                _e.U8(0x47);                             // LD B, A  (tentative low byte)
                _e.U8(0xAF);                             // XOR A
                _e.U8(0x4F);                             // LD C, A  (high-byte accumulator = 0)
                for (int k = 1; k < n; k++)
                {
                    LoadByteToA(b.Right, k);
                    _e.U8(0xB1);                         // OR C
                    _e.U8(0x4F);                         // LD C, A
                }
                var sat = new Label();
                var counted = new Label();
                _e.U8(0x79); _e.U8(0xB7);                // LD A, C ; OR A  (Z iff no high bits)
                _e.Jump(0xC2, sat);                      // JP NZ, sat
                _e.U8(0x78);                             // LD A, B
                _e.U8(0xFE); _e.U8((byte)width);         // CP width
                _e.Jump(0xDA, counted);                  // JP C, counted
                _e.Place(sat);
                _e.U8(0x06); _e.U8((byte)width);         // LD B, width
                _e.Place(counted);
                _e.U8(0x78);                             // LD A, B
                StoreAToAddr(RtBits);
            }

            string routine = b.Op switch
            {
                IrBinaryOp.Shl => "shl_wide",
                IrBinaryOp.LShr => "lshr_wide",
                _ => "ashr_wide",
            };
            _e.Jump(0xCD, _e.RoutineLabel(routine));
            CopyFromScratch(RtOpA, dst, n);
        }

        /// <summary>Copy the N low bytes of a value into fixed scratch at <paramref name="scratch"/>.</summary>
        private void CopyToScratch(IrValue value, int scratch, int n)
        {
            for (int k = 0; k < n; k++)
            {
                LoadByteToA(value, k);
                StoreAToAddr(scratch + k);
            }
        }

        /// <summary>Copy N bytes from fixed scratch at <paramref name="scratch"/> into a destination slot.</summary>
        private void CopyFromScratch(int scratch, int dst, int n)
        {
            for (int k = 0; k < n; k++)
            {
                LoadAFromAddr(scratch + k);
                StoreAToAddr(dst + k);
            }
        }

        private void LoadWorking(IrValue value, int n)
        {
            LoadByteToA(value, 0);
            _e.U8(0x5F);                 // LD E, A  (low byte)
            if (n == 2)
            {
                LoadByteToA(value, 1);
                _e.U8(0x57);             // LD D, A  (high byte)
            }
        }

        private void StoreWorking(int dst, int n)
        {
            _e.U8(0x7B);                 // LD A, E
            StoreAToAddr(dst);
            if (n == 2)
            {
                _e.U8(0x7A);             // LD A, D
                StoreAToAddr(dst + 1);
            }
        }

        private void ShiftWorkingOnce(IrBinaryOp op, int n)
        {
            switch (op)
            {
                case IrBinaryOp.Shl:
                    _e.U8(0xCB); _e.U8(0x23);                 // SLA E
                    if (n == 2) { _e.U8(0xCB); _e.U8(0x12); } // RL D
                    break;
                case IrBinaryOp.LShr:
                    if (n == 2) { _e.U8(0xCB); _e.U8(0x3A); } // SRL D
                    _e.U8(0xCB); _e.U8(n == 2 ? 0x1B : 0x3B); // RR E (i16) / SRL E (i8)
                    break;
                case IrBinaryOp.AShr:
                    if (n == 2) { _e.U8(0xCB); _e.U8(0x2A); } // SRA D
                    _e.U8(0xCB); _e.U8(n == 2 ? 0x1B : 0x2B); // RR E (i16) / SRA E (i8)
                    break;
                default:
                    throw new NotSupportedException($"not a shift: {op}");
            }
        }

        /// <summary>
        /// Lower multiply/divide/remainder to the shared runtime routines. Operands are widened to
        /// 16 bits (sign-extended for signed divide/remainder, zero-extended otherwise) and passed
        /// in DE (left) and BC (right); the result comes back in DE (quotient) or HL (product /
        /// remainder) and is truncated back to the operation width.
        /// </summary>
        private void EmitMulDivRem(BinaryInstruction b)
        {
            int n = SizeOf(b.Type);
            if (n > 2)
            {
                EmitWideMulDivRem(b, n);
                return;
            }
            int dst = _slot[b];
            bool signedDiv = b.Op is IrBinaryOp.SDiv or IrBinaryOp.SRem;

            LoadToPair(b.Left, n, hi: 0x57, lo: 0x5F, signedDiv);   // -> D:E
            LoadToPair(b.Right, n, hi: 0x47, lo: 0x4F, signedDiv);  // -> B:C

            string routine = b.Op switch
            {
                IrBinaryOp.Mul => "mul16",
                IrBinaryOp.UDiv or IrBinaryOp.URem => "udivmod16",
                _ => "sdivmod16",
            };
            _e.Jump(0xCD, _e.RoutineLabel(routine));

            bool resultInHL = b.Op is IrBinaryOp.Mul or IrBinaryOp.URem or IrBinaryOp.SRem;
            if (resultInHL)
                StoreRegPair(dst, n, hi: 0x7C, lo: 0x7D);  // LD A,H / LD A,L
            else
                StoreRegPair(dst, n, hi: 0x7A, lo: 0x7B);  // LD A,D / LD A,E
        }

        /// <summary>Load a value into a register pair, widening an i8 to 16 bits.</summary>
        /// <param name="lo">opcode for <c>LD lo, A</c>; <param name="hi">opcode for <c>LD hi, A</c>.</param>
        private void LoadToPair(IrValue value, int n, int hi, int lo, bool signExtend)
        {
            LoadByteToA(value, 0);
            _e.U8(lo);
            if (n == 2)
            {
                LoadByteToA(value, 1);
                _e.U8(hi);
            }
            else if (signExtend)
            {
                LoadByteToA(value, 0);
                _e.U8(0x87);   // ADD A, A  -> carry = sign bit
                _e.U8(0x9F);   // SBC A, A  -> 0xFF / 0x00
                _e.U8(hi);
            }
            else
            {
                _e.U8(hi == 0x57 ? 0x16 : 0x06); // LD D,0 / LD B,0
                _e.U8(0x00);
            }
        }

        /// <summary>Store a register pair to a slot, low byte first.</summary>
        private void StoreRegPair(int dst, int n, int hi, int lo)
        {
            _e.U8(lo);
            StoreAToAddr(dst);
            if (n == 2)
            {
                _e.U8(hi);
                StoreAToAddr(dst + 1);
            }
        }

        private void EmitCompare(CompareInstruction c)
        {
            var (pred, swap, signed) = Normalize(c.Op);
            IrValue left = swap ? c.Right : c.Left;
            IrValue right = swap ? c.Left : c.Right;
            int n = SizeOf(c.Left.Type);
            int dst = _slot[c];
            bool rightConst = right is IrConstInt;

            int falseJump;
            if (pred is IrCompareOp.Ult or IrCompareOp.Uge)
            {
                // Full-width subtract; the final carry is the borrow (left < right). For a signed
                // comparison, flip the sign bit of the top byte of both operands so the signed
                // ordering becomes the same unsigned/borrow test. The flip must happen *before* the
                // borrow chain — an inline XOR would clear the carry mid-chain and drop the borrow.
                if (signed)
                {
                    LoadByteToA(left, n - 1); _e.U8(0xEE); _e.U8(0x80); StoreAToAddr(RtCmpLeft);
                    if (!rightConst) { LoadByteToA(right, n - 1); _e.U8(0xEE); _e.U8(0x80); StoreAToAddr(RtCmpRight); }
                }
                for (int k = 0; k < n; k++)
                {
                    bool top = signed && k == n - 1;
                    if (rightConst)
                    {
                        if (top) LoadAFromAddr(RtCmpLeft); else LoadByteToA(left, k);
                        _e.U8(k == 0 ? 0xD6 : 0xDE);              // SUB d8 / SBC A, d8
                        _e.U8((byte)(ByteOf(right, k) ^ (top ? 0x80 : 0x00)));
                    }
                    else
                    {
                        if (top) LoadAFromAddr(RtCmpRight); else LoadByteToA(right, k);
                        _e.U8(0x47);                              // LD B, A
                        if (top) LoadAFromAddr(RtCmpLeft); else LoadByteToA(left, k);
                        _e.U8(k == 0 ? 0x90 : 0x98);              // SUB B / SBC A, B
                    }
                }
                falseJump = pred == IrCompareOp.Ult ? 0xD2 /*JP NC*/ : 0xDA /*JP C*/;
            }
            else
            {
                // Eq/Ne: OR together the per-byte XOR differences; Z is set iff all bytes match.
                for (int k = 0; k < n; k++)
                {
                    if (rightConst)
                    {
                        LoadByteToA(left, k);
                        _e.U8(0xEE);                   // XOR d8
                        _e.U8(ByteOf(right, k));
                    }
                    else
                    {
                        LoadByteToB(right, k);
                        LoadByteToA(left, k);
                        _e.U8(0xA8);                   // XOR B
                    }
                    if (k == 0)
                    {
                        _e.U8(0x4F);                   // LD C, A
                    }
                    else
                    {
                        _e.U8(0xB1);                   // OR C
                        _e.U8(0x4F);                   // LD C, A
                    }
                }
                falseJump = pred == IrCompareOp.Eq ? 0xC2 /*JP NZ*/ : 0xCA /*JP Z*/;
            }

            MaterializeBoolean(falseJump, dst);
        }

        /// <summary>A = 1 if the predicate holds (flags already set), else 0; stored to <paramref name="dst"/>.</summary>
        private void MaterializeBoolean(int falseJumpOpcode, int dst)
        {
            var done = new Label();
            _e.U8(0x3E); _e.U8(0x00);          // LD A, 0     (does not disturb flags)
            _e.Jump(falseJumpOpcode, done);    // predicate false -> keep 0
            _e.U8(0x3E); _e.U8(0x01);          // LD A, 1
            _e.Place(done);
            StoreAToAddr(dst);
        }

        private void EmitConv(ConvInstruction cv)
        {
            int srcBytes = SizeOf(cv.Operand.Type);
            int dstBytes = SizeOf(cv.Type);
            int dst = _slot[cv];

            switch (cv.Op)
            {
                case IrConvOp.Bitcast: // same-size reinterpret: copy the bytes through
                case IrConvOp.Trunc:
                    for (int k = 0; k < dstBytes; k++)
                    {
                        LoadByteToA(cv.Operand, k);
                        StoreAToAddr(dst + k);
                    }
                    break;

                case IrConvOp.ZExt:
                    for (int k = 0; k < srcBytes; k++)
                    {
                        LoadByteToA(cv.Operand, k);
                        StoreAToAddr(dst + k);
                    }
                    for (int k = srcBytes; k < dstBytes; k++)
                    {
                        _e.U8(0x3E); _e.U8(0x00);   // LD A, 0
                        StoreAToAddr(dst + k);
                    }
                    break;

                case IrConvOp.SExt:
                    for (int k = 0; k < srcBytes; k++)
                    {
                        LoadByteToA(cv.Operand, k);
                        StoreAToAddr(dst + k);
                    }
                    LoadByteToA(cv.Operand, srcBytes - 1);
                    _e.U8(0x87);                    // ADD A, A  -> carry = sign bit
                    _e.U8(0x9F);                    // SBC A, A  -> A = 0xFF if sign else 0x00
                    for (int k = srcBytes; k < dstBytes; k++)
                        StoreAToAddr(dst + k);       // LD (nn), A preserves A
                    break;

                default:
                    throw new NotSupportedException($"SM83 backend cannot lower conversion {cv.Op}.");
            }
        }

        // ---- Memory --------------------------------------------------------

        private void EmitLoad(LoadInstruction l)
        {
            int dst = _slot[l];
            int n = SizeOf(l.Type);

            if (TryStaticAddr(l.Pointer, out int addr))
            {
                for (int k = 0; k < n; k++)
                {
                    LoadAFromAddr(addr + k);
                    StoreAToAddr(dst + k);
                }
                return;
            }

            LoadPointerToHL(l.Pointer);
            for (int k = 0; k < n; k++)
            {
                _e.U8(0x7E);                     // LD A, (HL)
                StoreAToAddr(dst + k);
                if (k < n - 1) _e.U8(0x23);      // INC HL
            }
        }

        private void EmitStore(StoreInstruction s)
        {
            int n = SizeOf(s.Value.Type);

            if (TryStaticAddr(s.Pointer, out int addr))
            {
                for (int k = 0; k < n; k++)
                {
                    LoadByteToA(s.Value, k);
                    StoreAToAddr(addr + k);
                }
                return;
            }

            LoadPointerToHL(s.Pointer);          // (LoadByteToA below only touches A, not HL)
            for (int k = 0; k < n; k++)
            {
                LoadByteToA(s.Value, k);
                _e.U8(0x77);                     // LD (HL), A
                if (k < n - 1) _e.U8(0x23);      // INC HL
            }
        }

        /// <summary>Compute a dynamic pointer <c>base + index * sizeof(element)</c> into its slot.</summary>
        private void EmitGep(GetElementPtrInstruction g)
        {
            int size = SizeOf(g.ElementType);

            LoadIndexToDE(g.Index);              // offset = index (widened to 16 bits)
            if (size != 1)
            {
                if (IsPowerOfTwo(size))
                {
                    for (int s = 0; s < Log2(size); s++)
                    {
                        _e.U8(0xCB); _e.U8(0x23);   // SLA E
                        _e.U8(0xCB); _e.U8(0x12);   // RL D   (DE <<= 1)
                    }
                }
                else
                {
                    _e.U8(0x01); _e.U16(size); // LD BC, size
                    _e.Jump(0xCD, _e.RoutineLabel("mul16"));          // HL = DE * size
                    _e.U8(0x54); _e.U8(0x5D);                         // LD D,H ; LD E,L  (offset -> DE)
                }
            }

            LoadPointerToHL(g.BasePointer);      // HL = base
            _e.U8(0x19);                         // ADD HL, DE
            StoreHLToSlot(_slot[g]);
        }

        private void LoadIndexToDE(IrValue index)
        {
            if (SizeOf(index.Type) > 2)
                throw new NotSupportedException("SM83 backend gep index must be <= 16-bit.");
            LoadByteToA(index, 0);
            _e.U8(0x5F);                         // LD E, A
            if (SizeOf(index.Type) == 2)
            {
                LoadByteToA(index, 1);
                _e.U8(0x57);                     // LD D, A
            }
            else
            {
                _e.U8(0x16); _e.U8(0x00);        // LD D, 0
            }
        }

        /// <summary>Load a pointer value into HL: a static address as an immediate, else from its slot.</summary>
        private void LoadPointerToHL(IrValue pointer)
        {
            if (TryStaticAddr(pointer, out int addr))
            {
                LdHL(_e, addr);   // LD HL, addr
            }
            else if (_slot.TryGetValue(pointer, out int slot))
            {
                LoadAFromAddr(slot); _e.U8(0x6F);       // LD A, (slot)   ; LD L, A
                LoadAFromAddr(slot + 1); _e.U8(0x67);   // LD A, (slot+1) ; LD H, A
            }
            else
            {
                throw new NotSupportedException("SM83 backend cannot resolve this pointer operand.");
            }
        }

        private void StoreHLToSlot(int slot)
        {
            _e.U8(0x7D); StoreAToAddr(slot);        // LD A, L ; store low
            _e.U8(0x7C); StoreAToAddr(slot + 1);    // LD A, H ; store high
        }

        private static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

        private static int Log2(int n)
        {
            int k = 0;
            while ((1 << k) < n) k++;
            return k;
        }

        // ---- Control flow --------------------------------------------------

        private void EmitRet(RetInstruction r)
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

        private void EmitIntrinsic(IntrinsicInstruction instr)
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
        private void EmitCall(CallInstruction call)
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

        private void EmitBr(IrBasicBlock source, BrInstruction br)
        {
            EmitPhiCopies(source, br.Target);
            _e.Jump(0xC3, _e.BlockLabel(br.Target)); // JP a16
        }

        private void EmitCondBr(IrBasicBlock source, CondBrInstruction cb)
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
        private void EmitSwitch(IrBasicBlock source, SwitchInstruction sw)
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
                _e.U8(0xEE); _e.U8(ByteOf(caseConst, k));  // XOR d8
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

        // ---- Byte-level helpers -------------------------------------------

        /// <summary>Load byte <paramref name="k"/> (0 = low) of a value into <c>A</c>.</summary>
        private void LoadByteToA(IrValue value, int k)
        {
            switch (value)
            {
                case IrConstInt c:
                    _e.U8(0x3E);                 // LD A, d8
                    _e.U8(ByteOf(value, k));
                    break;
                default:
                    if (_slot.TryGetValue(value, out int addr))
                    {
                        LoadAFromAddr(addr + k);
                    }
                    else if (TryStaticAddr(value, out int ptr))
                    {
                        _e.U8(0x3E);             // pointer literal: LD A, <byte k of address>
                        _e.U8((byte)(ptr >> (8 * k)));
                    }
                    else
                    {
                        throw new NotSupportedException(
                            "SM83 backend operand must be a constant, parameter, prior result, or global.");
                    }
                    break;
            }
        }

        private void LoadByteToB(IrValue value, int k)
        {
            LoadByteToA(value, k);
            _e.U8(0x47); // LD B, A
        }

        private void LoadAFromAddr(int addr) => _e.LoadA(addr);   // may be elided if A already holds it

        private void StoreAToAddr(int addr) => _e.StoreA(addr);

        private static byte ByteOf(IrValue value, int k) =>
            value is IrConstInt c
                // Value is a 64-bit long; bytes 8..15 of a wider (i128) constant are its sign extension.
                // (A raw `c.Value >> (8*k)` would be wrong: C# masks the shift count to 63, so k>=8 would
                // replicate the low bytes instead of extending.)
                ? (byte)(k < 8 ? c.Value >> (8 * k) : c.Value >> 63)
                : throw new NotSupportedException("expected a constant operand.");

        /// <summary>
        /// Reduce any predicate to a base carry/eq test plus operand-swap and sign flags:
        /// <c>Ugt/Ule</c> swap into <c>Ult/Uge</c>; signed predicates map to the same base with
        /// <c>Signed = true</c> (handled by flipping the top byte's sign bit).
        /// </summary>
        private static (IrCompareOp Pred, bool Swap, bool Signed) Normalize(IrCompareOp op) => op switch
        {
            IrCompareOp.Eq => (IrCompareOp.Eq, false, false),
            IrCompareOp.Ne => (IrCompareOp.Ne, false, false),
            IrCompareOp.Ult => (IrCompareOp.Ult, false, false),
            IrCompareOp.Uge => (IrCompareOp.Uge, false, false),
            IrCompareOp.Ugt => (IrCompareOp.Ult, true, false),  // a > b  <=>  b < a
            IrCompareOp.Ule => (IrCompareOp.Uge, true, false),  // a <= b <=>  b >= a
            IrCompareOp.Slt => (IrCompareOp.Ult, false, true),
            IrCompareOp.Sge => (IrCompareOp.Uge, false, true),
            IrCompareOp.Sgt => (IrCompareOp.Ult, true, true),   // a > b  <=>  b < a
            IrCompareOp.Sle => (IrCompareOp.Uge, true, true),   // a <= b <=>  b >= a
            _ => throw new NotSupportedException($"SM83 backend cannot lower comparison {op}."),
        };

        private static byte AluImmOpcode(IrBinaryOp op, int k) => op switch
        {
            IrBinaryOp.Add => (byte)(k == 0 ? 0xC6 : 0xCE), // ADD A,d8 / ADC A,d8
            IrBinaryOp.Sub => (byte)(k == 0 ? 0xD6 : 0xDE), // SUB d8   / SBC A,d8
            IrBinaryOp.And => 0xE6,
            IrBinaryOp.Or => 0xF6,
            IrBinaryOp.Xor => 0xEE,
            _ => throw new NotSupportedException($"SM83 backend does not support '{op}'."),
        };

        private static byte AluRegOpcode(IrBinaryOp op, int k) => op switch
        {
            IrBinaryOp.Add => (byte)(k == 0 ? 0x80 : 0x88), // ADD A,B / ADC A,B
            IrBinaryOp.Sub => (byte)(k == 0 ? 0x90 : 0x98), // SUB B   / SBC A,B
            IrBinaryOp.And => 0xA0,
            IrBinaryOp.Or => 0xB0,
            IrBinaryOp.Xor => 0xA8,
            _ => throw new NotSupportedException($"SM83 backend does not support '{op}'."),
        };
    }
}
