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
        CheckNoRecursion(module);

        // Give every function a disjoint WRAM frame so a caller's live values and a callee's
        // storage never overlap (correct for a non-recursive call graph; frames are not yet
        // reused across functions that can't be live simultaneously).
        var allocations = new Dictionary<IrFunction, FunctionAllocation>(ReferenceEqualityComparer.Instance);
        int wram = WramBase;
        foreach (var fn in module.Functions)
        {
            if (fn.IsExternal)
                continue;
            var allocation = FunctionAllocation.For(fn, wram);
            allocations[fn] = allocation;
            wram = allocation.FrameEnd;
        }

        var emitter = new Emitter();
        var symbols = new List<SymbolData>();

        foreach (var fn in module.Functions)
        {
            if (fn.IsExternal)
                continue;

            int funcStart = CodeBase + emitter.Code.Count;
            new FunctionEmitter(emitter, fn, allocations).Compile();
            symbols.Add(new SymbolData(
                fn.Name, SymbolKind.Label, SymbolVisibility.Exported, CodeSectionName, funcStart));
        }

        emitter.Resolve(CodeBase);

        var section = new SectionData(
            CodeSectionName, SectionType.Rom0, fixedAddress: CodeBase, bank: 0,
            data: emitter.Code.ToArray(), patches: Array.Empty<PatchEntry>());

        return new EmitModel([section], symbols, Array.Empty<Diagnostic>());
    }

    /// <summary>Static frame allocation cannot support recursion; reject cyclic call graphs.</summary>
    private static void CheckNoRecursion(IrModule module)
    {
        var callees = new Dictionary<IrFunction, List<IrFunction>>(ReferenceEqualityComparer.Instance);
        foreach (var fn in module.Functions)
        {
            var list = new List<IrFunction>();
            foreach (var block in fn.Blocks)
                foreach (var instr in block.Instructions)
                    if (instr is CallInstruction call && !call.Callee.IsExternal)
                        list.Add(call.Callee);
            callees[fn] = list;
        }

        var state = new Dictionary<IrFunction, int>(ReferenceEqualityComparer.Instance); // 0 unseen, 1 on-stack, 2 done

        bool HasCycle(IrFunction fn)
        {
            state.TryGetValue(fn, out int s);
            if (s == 1) return true;
            if (s == 2) return false;
            state[fn] = 1;
            if (callees.TryGetValue(fn, out var next))
                foreach (var callee in next)
                    if (HasCycle(callee))
                        return true;
            state[fn] = 2;
            return false;
        }

        foreach (var fn in module.Functions)
            if (HasCycle(fn))
                throw new NotSupportedException(
                    "SM83 backend does not support recursion (functions use static WRAM frames).");
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

    /// <summary>
    /// The WRAM layout of one function's frame: fixed addresses for parameters and SSA values,
    /// compile-time addresses for <c>alloca</c>/constant-<c>gep</c> pointers, and scratch for
    /// phi-cycle breaking. Computed for the whole module before any code is emitted so a caller
    /// knows where to place a callee's arguments.
    /// </summary>
    private sealed class FunctionAllocation
    {
        public required Dictionary<IrValue, int> Slot { get; init; }
        public required Dictionary<IrValue, int> StaticAddr { get; init; }
        public required int PhiTempBase { get; init; }
        public required int FrameEnd { get; init; }

        public static FunctionAllocation For(IrFunction fn, int baseAddr)
        {
            var slot = new Dictionary<IrValue, int>(ReferenceEqualityComparer.Instance);
            var staticAddr = new Dictionary<IrValue, int>(ReferenceEqualityComparer.Instance);
            int wram = baseAddr;

            foreach (var p in fn.Parameters)
            {
                slot[p] = wram;
                wram += SizeOf(p.Type);
            }

            int phiCount = 0;
            foreach (var block in fn.Blocks)
                foreach (var instr in block.Instructions)
                {
                    switch (instr)
                    {
                        case AllocaInstruction a:
                            staticAddr[a] = wram;
                            wram += SizeOf(a.Allocated);
                            break;
                        case GetElementPtrInstruction g:
                            staticAddr[g] = (staticAddr.TryGetValue(g.BasePointer, out int b)
                                ? b
                                : throw new NotSupportedException(
                                    "MVP SM83 backend supports only static pointers (alloca / constant-index gep)."))
                                + ConstIndex(g) * SizeOf(g.ElementType);
                            break;
                        default:
                            if (instr is PhiInstruction)
                                phiCount++;
                            if (instr.Type.Kind != IrTypeKind.Void)
                            {
                                slot[instr] = wram;
                                wram += SizeOf(instr.Type);
                            }
                            break;
                    }
                }

            int phiTempBase = wram;
            wram += 2 * phiCount; // at most one temp (<= 2 bytes) per phi

            return new FunctionAllocation
            {
                Slot = slot,
                StaticAddr = staticAddr,
                PhiTempBase = phiTempBase,
                FrameEnd = wram,
            };
        }

        private static int ConstIndex(GetElementPtrInstruction g) =>
            g.Index is IrConstInt c
                ? (int)c.Value
                : throw new NotSupportedException("MVP SM83 backend supports constant gep indices only.");
    }

    /// <summary>Lowers one function into the shared emitter using the static-allocation model.</summary>
    private sealed class FunctionEmitter
    {
        private readonly Emitter _e;
        private readonly IrFunction _fn;
        private readonly IReadOnlyDictionary<IrFunction, FunctionAllocation> _allocations;
        private readonly Dictionary<IrValue, int> _slot;
        private readonly Dictionary<IrValue, int> _staticAddr;
        private readonly int _phiTempBase;

        public FunctionEmitter(
            Emitter emitter, IrFunction fn, IReadOnlyDictionary<IrFunction, FunctionAllocation> allocations)
        {
            _e = emitter;
            _fn = fn;
            _allocations = allocations;
            var allocation = allocations[fn];
            _slot = allocation.Slot;
            _staticAddr = allocation.StaticAddr;
            _phiTempBase = allocation.PhiTempBase;
        }

        public void Compile()
        {
            foreach (var block in _fn.Blocks)
            {
                _e.Place(_e.BlockLabel(block));
                foreach (var instr in block.Instructions)
                    EmitInstruction(block, instr);
            }
        }

        private int StaticBase(IrValue pointer) =>
            _staticAddr.TryGetValue(pointer, out int addr)
                ? addr
                : throw new NotSupportedException(
                    "MVP SM83 backend supports only static pointers (alloca / constant-index gep).");

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
                case SwitchInstruction sw: EmitSwitch(block, sw); break;
                case CallInstruction call: EmitCall(call); break;
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

            // Variable amount: count in B, value in the working register(s).
            LoadByteToA(b.Right, 0);
            _e.U8(0x47);                 // LD B, A
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
                // comparison, flipping the sign bit of the top byte of both operands turns the
                // signed ordering into the same unsigned/borrow test.
                for (int k = 0; k < n; k++)
                {
                    bool flip = signed && k == n - 1;
                    if (rightConst)
                    {
                        LoadByteToA(left, k);
                        if (flip) { _e.U8(0xEE); _e.U8(0x80); }   // XOR 0x80
                        _e.U8(k == 0 ? 0xD6 : 0xDE);              // SUB d8 / SBC A, d8
                        _e.U8((byte)(ByteOf(right, k) ^ (flip ? 0x80 : 0x00)));
                    }
                    else
                    {
                        LoadByteToA(right, k);
                        if (flip) { _e.U8(0xEE); _e.U8(0x80); }
                        _e.U8(0x47);                              // LD B, A
                        LoadByteToA(left, k);
                        if (flip) { _e.U8(0xEE); _e.U8(0x80); }
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

            var calleeAllocation = _allocations[callee];
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                var param = callee.Parameters[i];
                int paramSlot = calleeAllocation.Slot[param];
                int n = SizeOf(param.Type);
                for (int k = 0; k < n; k++)
                {
                    LoadByteToA(call.Arguments[i], k);
                    StoreAToAddr(paramSlot + k);
                }
            }

            _e.Jump(0xCD, _e.BlockLabel(callee.EntryBlock)); // CALL a16

            if (call.Type.Kind == IrTypeKind.Void)
                return;

            int dst = _slot[call];
            switch (SizeOf(call.Type))
            {
                case 1:
                    StoreAToAddr(dst);                    // result in A
                    break;
                case 2:
                    _e.U8(0x7D); StoreAToAddr(dst);       // LD A, L ; store low
                    _e.U8(0x7C); StoreAToAddr(dst + 1);   // LD A, H ; store high
                    break;
                default:
                    throw new NotSupportedException(
                        $"SM83 backend can only capture i8/i16 return values, not {call.Type}.");
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
