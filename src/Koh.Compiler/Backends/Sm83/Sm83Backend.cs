using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Symbols;

namespace Koh.Compiler.Backends.Sm83;

/// <summary>
/// The hand-written SM83 backend (Phase 2). Correctness-first, non-optimizing code generation.
///
/// Allocation model — the simplest form of the NESFab-style static allocation the design calls
/// for: every parameter and every value-producing instruction gets fixed WRAM storage, and
/// every operation flows through the accumulator. Pointers from <c>alloca</c> and
/// constant-index <c>gep</c> are compile-time-known addresses, so <c>load</c>/<c>store</c> use
/// absolute addressing. It emits deliberately poor code; the goal is a trustworthy, observable
/// pipeline, not tight output.
///
/// Supported today: <c>i8</c>/<c>i16</c> arithmetic (add/sub via ADC/SBC chains, and/or/xor),
/// unsigned + eq/ne comparisons, integer conversions (trunc/zext/sext), control flow
/// (br/condbr/phi with critical-edge-split phi copies), and static-address memory ops. Not yet:
/// signed comparisons, dynamic-pointer load/store, <c>switch</c>, calls, multiply/divide/shift,
/// and instruction selection through <see cref="Koh.Core.Encoding.Sm83InstructionTable"/>.
///
/// Calling convention (MVP): parameters occupy WRAM from <see cref="WramBase"/> in declaration
/// order; an <c>i8</c> result is returned in <c>A</c>, an <c>i16</c> result in <c>HL</c>.
/// Unsupported IR throws <see cref="NotSupportedException"/> so the boundary stays explicit.
/// </summary>
public sealed class Sm83Backend : IBackend
{
    /// <summary>Fixed ROM address the emitted code section is placed at.</summary>
    public const int CodeBase = 0x0150;

    /// <summary>First WRAM byte used for parameters and statically-allocated SSA storage.</summary>
    public const int WramBase = 0xC000;

    private const string CodeSectionName = "CODE";

    public string Name => "sm83";

    public TargetInfo Target => TargetInfo.Sm83;

    public EmitModel Compile(IrModule module, DiagnosticBag diagnostics)
    {
        var emitter = new Emitter();
        var symbols = new List<SymbolData>();

        foreach (var fn in module.Functions)
        {
            if (fn.IsExternal)
                continue;

            int funcStart = CodeBase + emitter.Code.Count;
            new FunctionEmitter(emitter, fn).Compile();
            symbols.Add(new SymbolData(
                fn.Name, SymbolKind.Label, SymbolVisibility.Exported, CodeSectionName, funcStart));
        }

        emitter.Resolve(CodeBase);

        var section = new SectionData(
            CodeSectionName, SectionType.Rom0, fixedAddress: CodeBase, bank: 0,
            data: emitter.Code.ToArray(), patches: Array.Empty<PatchEntry>());

        return new EmitModel([section], symbols, Array.Empty<Diagnostic>());
    }

    internal static int SizeOf(IrType type) => type.Kind switch
    {
        IrTypeKind.Void => 0,
        IrTypeKind.Int => (type.Bits + 7) / 8,
        IrTypeKind.Pointer => 2,
        IrTypeKind.Array => type.ArrayLength * SizeOf(type.Element!),
        _ => throw new NotSupportedException($"SM83 backend cannot size type {type}."),
    };

    /// <summary>A forward reference resolved to an absolute address once its target is placed.</summary>
    private sealed class Label { public int Offset = -1; }

    /// <summary>A growable code buffer with block labels and absolute-address fixups.</summary>
    private sealed class Emitter
    {
        public readonly List<byte> Code = [];
        private readonly Dictionary<IrBasicBlock, Label> _blocks = new(ReferenceEqualityComparer.Instance);
        private readonly List<(int Pos, Label Target)> _fixups = [];

        public void U8(int value) => Code.Add((byte)value);

        public Label BlockLabel(IrBasicBlock block)
        {
            if (!_blocks.TryGetValue(block, out var label))
                _blocks[block] = label = new Label();
            return label;
        }

        public void Place(Label label) => label.Offset = Code.Count;

        /// <summary>Emit a jump opcode plus a two-byte placeholder patched to the label's address.</summary>
        public void Jump(int opcode, Label target)
        {
            Code.Add((byte)opcode);
            _fixups.Add((Code.Count, target));
            Code.Add(0);
            Code.Add(0);
        }

        public void Resolve(int codeBase)
        {
            foreach (var (pos, target) in _fixups)
            {
                if (target.Offset < 0)
                    throw new InvalidOperationException("unplaced jump target");
                int addr = codeBase + target.Offset;
                Code[pos] = (byte)(addr & 0xFF);
                Code[pos + 1] = (byte)(addr >> 8);
            }
        }
    }

    /// <summary>Lowers one function into the shared emitter using the static-allocation model.</summary>
    private sealed class FunctionEmitter
    {
        private readonly Emitter _e;
        private readonly IrFunction _fn;
        private readonly Dictionary<IrValue, int> _slot = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<IrValue, int> _staticAddr = new(ReferenceEqualityComparer.Instance);
        private int _phiTempBase;

        public FunctionEmitter(Emitter emitter, IrFunction fn)
        {
            _e = emitter;
            _fn = fn;
        }

        public void Compile()
        {
            Allocate();
            foreach (var block in _fn.Blocks)
            {
                _e.Place(_e.BlockLabel(block));
                foreach (var instr in block.Instructions)
                    EmitInstruction(block, instr);
            }
        }

        private void Allocate()
        {
            int wram = WramBase;

            foreach (var p in _fn.Parameters)
            {
                _slot[p] = wram;
                wram += SizeOf(p.Type);
            }

            foreach (var block in _fn.Blocks)
                foreach (var instr in block.Instructions)
                {
                    switch (instr)
                    {
                        case AllocaInstruction a:
                            _staticAddr[a] = wram;
                            wram += SizeOf(a.Allocated);
                            break;
                        case GetElementPtrInstruction g:
                            _staticAddr[g] = StaticBase(g.BasePointer)
                                + ConstIndex(g) * SizeOf(g.ElementType);
                            break;
                        default:
                            if (instr.Type.Kind != IrTypeKind.Void)
                            {
                                _slot[instr] = wram;
                                wram += SizeOf(instr.Type);
                            }
                            break;
                    }
                }

            // Reserve scratch for breaking phi copy cycles: at most one temp (<= 2 bytes) per phi.
            int phiCount = 0;
            foreach (var block in _fn.Blocks)
                foreach (var instr in block.Instructions)
                    if (instr is PhiInstruction)
                        phiCount++;
            _phiTempBase = wram;
            wram += 2 * phiCount;
        }

        private int StaticBase(IrValue pointer) =>
            _staticAddr.TryGetValue(pointer, out int addr)
                ? addr
                : throw new NotSupportedException(
                    "MVP SM83 backend supports only static pointers (alloca / constant-index gep).");

        private static int ConstIndex(GetElementPtrInstruction g) =>
            g.Index is IrConstInt c
                ? (int)c.Value
                : throw new NotSupportedException("MVP SM83 backend supports constant gep indices only.");

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
                case GetElementPtrInstruction: break;        // static address pre-assigned
                case PhiInstruction: break;                  // realized by predecessor edge copies
                case RetInstruction r: EmitRet(r); break;
                case BrInstruction br: EmitBr(block, br); break;
                case CondBrInstruction cb: EmitCondBr(block, cb); break;
                default:
                    throw new NotSupportedException(
                        $"MVP SM83 backend does not support '{instr.Mnemonic}' (in '@{_fn.Name}').");
            }
        }

        // ---- Arithmetic ----------------------------------------------------

        private void EmitBinary(BinaryInstruction b)
        {
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

        private void EmitCompare(CompareInstruction c)
        {
            var (pred, swap) = Normalize(c.Op);
            IrValue left = swap ? c.Right : c.Left;
            IrValue right = swap ? c.Left : c.Right;
            int n = SizeOf(c.Left.Type);
            int dst = _slot[c];
            bool rightConst = right is IrConstInt;

            int falseJump;
            if (pred is IrCompareOp.Ult or IrCompareOp.Uge)
            {
                // Full-width subtract; the final carry is the unsigned borrow (left < right).
                for (int k = 0; k < n; k++)
                {
                    if (rightConst)
                    {
                        LoadByteToA(left, k);
                        _e.U8(k == 0 ? 0xD6 : 0xDE);   // SUB d8 / SBC A, d8
                        _e.U8(ByteOf(right, k));
                    }
                    else
                    {
                        LoadByteToB(right, k);
                        LoadByteToA(left, k);
                        _e.U8(k == 0 ? 0x90 : 0x98);   // SUB B / SBC A, B
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
            int addr = StaticBase(l.Pointer);
            int dst = _slot[l];
            int n = SizeOf(l.Type);
            for (int k = 0; k < n; k++)
            {
                LoadAFromAddr(addr + k);
                StoreAToAddr(dst + k);
            }
        }

        private void EmitStore(StoreInstruction s)
        {
            int addr = StaticBase(s.Pointer);
            int n = SizeOf(s.Value.Type);
            for (int k = 0; k < n; k++)
            {
                LoadByteToA(s.Value, k);
                StoreAToAddr(addr + k);
            }
        }

        // ---- Control flow --------------------------------------------------

        private void EmitRet(RetInstruction r)
        {
            if (r.Value is null)
            {
                _e.U8(0xC9); // RET
                return;
            }

            int n = SizeOf(r.Value.Type);
            switch (n)
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
                default:
                    throw new NotSupportedException(
                        $"SM83 backend can only return i8 (A) or i16 (HL), not {r.Value.Type}.");
            }
            _e.U8(0xC9); // RET
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
                    bool neededAsSource = pending.Any(o =>
                        o != c && o.Src is not null && ReferenceEquals(o.Src, c.DestPhi));
                    if (!neededAsSource)
                    {
                        EmitMove(c);
                        pending.RemoveAt(i);
                    }
                }

                if (pending.Count == before)
                {
                    // Every remaining copy is part of a cycle: stage one destination through a temp,
                    // redirect its readers there, then let the next pass drain the now-open chain.
                    var c = pending[0];
                    int tempAddr = temp;
                    temp += c.N;
                    for (int k = 0; k < c.N; k++)
                    {
                        LoadAFromAddr(c.DestSlot + k);
                        StoreAToAddr(tempAddr + k);
                    }
                    foreach (var o in pending)
                        if (o != c && o.Src is not null && ReferenceEquals(o.Src, c.DestPhi))
                        {
                            o.Src = null;
                            o.TempSrc = tempAddr;
                        }
                }
            }
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
                    else if (_staticAddr.TryGetValue(value, out int ptr))
                    {
                        _e.U8(0x3E);             // pointer literal: LD A, <byte k of address>
                        _e.U8((byte)(ptr >> (8 * k)));
                    }
                    else
                    {
                        throw new NotSupportedException(
                            "SM83 backend operand must be a constant, parameter, or prior result.");
                    }
                    break;
            }
        }

        private void LoadByteToB(IrValue value, int k)
        {
            LoadByteToA(value, k);
            _e.U8(0x47); // LD B, A
        }

        private void LoadAFromAddr(int addr)
        {
            _e.U8(0xFA);                 // LD A, (a16)
            _e.U8(addr & 0xFF);
            _e.U8(addr >> 8);
        }

        private void StoreAToAddr(int addr)
        {
            _e.U8(0xEA);                 // LD (a16), A
            _e.U8(addr & 0xFF);
            _e.U8(addr >> 8);
        }

        private static byte ByteOf(IrValue value, int k) =>
            value is IrConstInt c
                ? (byte)(c.Value >> (8 * k))
                : throw new NotSupportedException("expected a constant operand.");

        private static (IrCompareOp Pred, bool Swap) Normalize(IrCompareOp op) => op switch
        {
            IrCompareOp.Eq => (IrCompareOp.Eq, false),
            IrCompareOp.Ne => (IrCompareOp.Ne, false),
            IrCompareOp.Ult => (IrCompareOp.Ult, false),
            IrCompareOp.Uge => (IrCompareOp.Uge, false),
            IrCompareOp.Ugt => (IrCompareOp.Ult, true),  // a > b  <=>  b < a
            IrCompareOp.Ule => (IrCompareOp.Uge, true),  // a <= b <=>  b >= a
            _ => throw new NotSupportedException($"SM83 backend does not support signed comparison {op}."),
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
