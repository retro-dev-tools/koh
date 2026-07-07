using Koh.Compiler.Ir;
using Koh.Core.Binding;

namespace Koh.Compiler.Backends.Sm83;

public sealed partial class Sm83Backend
{
    /// <summary>Lowers arithmetic, shift, comparison, and conversion instructions.</summary>
    internal sealed class ArithmeticEmitter : EmitBase
    {
        public ArithmeticEmitter(
            Emitter emitter,
            IrFunction fn,
            IReadOnlyDictionary<IrFunction, FunctionAllocation> allocations,
            IReadOnlyDictionary<IrGlobal, int> globals,
            IReadOnlySet<IrFunction> recursive,
            bool isEntry,
            int softStackBase,
            IReadOnlySet<IrFunction>? banked = null)
            : base(emitter, fn, allocations, globals, recursive, isEntry, softStackBase, banked) { }

        // ---- Arithmetic ----------------------------------------------------

        public void EmitBinary(BinaryInstruction b)
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
                    _e.U8(Sm83Ops.AluImmOpcode(b.Op, k));
                    _e.U8(Sm83Ops.ByteOf(b.Right, k));
                }
                else
                {
                    LoadByteToB(b.Right, k);
                    LoadByteToA(b.Left, k);
                    _e.U8(Sm83Ops.AluRegOpcode(b.Op, k));
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

        public void EmitCompare(CompareInstruction c)
        {
            var (pred, swap, signed) = Sm83Ops.Normalize(c.Op);
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
                        _e.U8((byte)(Sm83Ops.ByteOf(right, k) ^ (top ? 0x80 : 0x00)));
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
                        _e.U8(Sm83Ops.ByteOf(right, k));
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

        public void EmitConv(ConvInstruction cv)
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
    }
}
