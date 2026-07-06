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

    /// <summary>Fixed ROM address of the read-only data section (initialized globals).</summary>
    public const int DataBase = 0x2000;

    /// <summary>First WRAM byte used for parameters and statically-allocated SSA storage.</summary>
    public const int WramBase = 0xC000;

    private const string CodeSectionName = "CODE";

    public string Name => "sm83";

    public TargetInfo Target => TargetInfo.Sm83;

    public EmitModel Compile(IrModule module, DiagnosticBag diagnostics)
    {
        CheckNoRecursion(module);

        // Assign global addresses. Initialized (or ROM-space) globals live in a fixed ROM data
        // section; RAM globals get fixed WRAM/HRAM/SRAM addresses. Function frames are placed
        // after the WRAM globals so nothing overlaps.
        var globalAddresses = new Dictionary<IrGlobal, int>(ReferenceEqualityComparer.Instance);
        var romData = new List<byte>();
        int wramGlobals = WramBase, hramGlobals = 0xFF80, sramGlobals = 0xA000;
        foreach (var g in module.Globals)
        {
            if (g.FixedAddress is int pinned)
            {
                globalAddresses[g] = pinned; // memory-mapped register / explicit placement
            }
            else if (g.AddressSpace == AddressSpace.Rom || g.Initializer is not null)
            {
                globalAddresses[g] = DataBase + romData.Count;
                var bytes = g.Initializer ?? new byte[SizeOf(g.Type)];
                romData.AddRange(bytes);
            }
            else if (g.AddressSpace == AddressSpace.Hram)
            {
                globalAddresses[g] = hramGlobals;
                hramGlobals += SizeOf(g.Type);
            }
            else if (g.AddressSpace == AddressSpace.Sram)
            {
                globalAddresses[g] = sramGlobals;
                sramGlobals += SizeOf(g.Type);
            }
            else
            {
                globalAddresses[g] = wramGlobals;
                wramGlobals += SizeOf(g.Type);
            }
        }

        // Give every function a disjoint WRAM frame so a caller's live values and a callee's
        // storage never overlap (correct for a non-recursive call graph; frames are not yet
        // reused across functions that can't be live simultaneously).
        var allocations = new Dictionary<IrFunction, FunctionAllocation>(ReferenceEqualityComparer.Instance);
        int wram = wramGlobals;
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

        // The cartridge boots into "main" (or the first function if there is none).
        var entryFunction = module.Functions.FirstOrDefault(f => !f.IsExternal && f.Name == "main")
            ?? module.Functions.FirstOrDefault(f => !f.IsExternal);
        int entryAddress = CodeBase;

        foreach (var fn in module.Functions)
        {
            if (fn.IsExternal)
                continue;

            int funcStart = CodeBase + emitter.Code.Count;
            if (ReferenceEquals(fn, entryFunction))
                entryAddress = funcStart;
            new FunctionEmitter(emitter, fn, allocations, globalAddresses).Compile();
            symbols.Add(new SymbolData(
                fn.Name, SymbolKind.Label, SymbolVisibility.Exported, CodeSectionName, funcStart));
        }

        // Emit runtime helper routines that generated code referenced (signed divide needs the
        // unsigned one). They are appended after all functions and only entered via CALL.
        if (emitter.NeededRoutines.Contains("sdivmod16"))
            emitter.NeededRoutines.Add("udivmod16");
        if (emitter.NeededRoutines.Contains("mul16")) EmitMul16(emitter);
        if (emitter.NeededRoutines.Contains("udivmod16")) EmitUDivMod16(emitter);
        if (emitter.NeededRoutines.Contains("sdivmod16")) EmitSDivMod16(emitter);

        emitter.Resolve(CodeBase);

        var sections = new List<SectionData>
        {
            new(CodeSectionName, SectionType.Rom0, fixedAddress: CodeBase, bank: 0,
                data: emitter.Code.ToArray(), patches: Array.Empty<PatchEntry>(),
                lineMap: emitter.LineMap),
        };
        if (romData.Count > 0)
            sections.Add(new SectionData(
                "RODATA", SectionType.Rom0, fixedAddress: DataBase, bank: 0,
                data: romData.ToArray(), patches: Array.Empty<PatchEntry>()));
        if (entryFunction is not null)
            sections.Add(new SectionData(
                "HEADER", SectionType.Rom0, fixedAddress: 0x0100, bank: 0,
                data: BuildHeader(entryAddress), patches: Array.Empty<PatchEntry>()));

        foreach (var (g, addr) in globalAddresses)
            symbols.Add(new SymbolData(
                g.Name, SymbolKind.Label, SymbolVisibility.Exported, CodeSectionName, addr));

        return new EmitModel(sections, symbols, Array.Empty<Diagnostic>());
    }

    /// <summary>The 48-byte Nintendo logo the boot ROM verifies at 0x0104.</summary>
    private static ReadOnlySpan<byte> NintendoLogo =>
    [
        0xCE, 0xED, 0x66, 0x66, 0xCC, 0x0D, 0x00, 0x0B, 0x03, 0x73, 0x00, 0x83, 0x00, 0x0C, 0x00, 0x0D,
        0x00, 0x08, 0x11, 0x1F, 0x88, 0x89, 0x00, 0x0E, 0xDC, 0xCC, 0x6E, 0xE6, 0xDD, 0xDD, 0xD9, 0x99,
        0xBB, 0xBB, 0x67, 0x63, 0x6E, 0x0E, 0xEC, 0xCC, 0xDD, 0xDC, 0x99, 0x9F, 0xBB, 0xB9, 0x33, 0x3E,
    ];

    /// <summary>
    /// Build the 80-byte cartridge header spanning 0x0100..0x014F: a <c>nop; jp entry</c> boot
    /// vector, the Nintendo logo, ROM-only cartridge fields, and the header checksum. This makes
    /// the emitted image a bootable cartridge rather than a bare code blob.
    /// </summary>
    private static byte[] BuildHeader(int entryAddress)
    {
        var header = new byte[0x50]; // 0x0100..0x014F

        header[0x00] = 0x00;                          // nop
        header[0x01] = 0xC3;                          // jp a16
        header[0x02] = (byte)(entryAddress & 0xFF);
        header[0x03] = (byte)(entryAddress >> 8);

        NintendoLogo.CopyTo(header.AsSpan(0x04));      // 0x0104..0x0133

        // Title bytes (0x0134..) left zero; cartridge type/ROM/RAM sizes 0 => ROM-only, 32 KB.
        // Header checksum over 0x0134..0x014C (indices 0x34..0x4C).
        byte checksum = 0;
        for (int i = 0x34; i <= 0x4C; i++)
            checksum = (byte)(checksum - header[i] - 1);
        header[0x4D] = checksum;

        return header;
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

    // Fixed scratch for the (non-reentrant) runtime routines.
    private const int RtCount = 0xDF00;     // division bit counter
    private const int RtSignRem = 0xDF01;   // signed division: remainder sign
    private const int RtSignQuot = 0xDF02;  // signed division: quotient sign

    /// <summary>__mul16: HL = DE * BC (low 16 bits) by shift-and-add. Sign-agnostic.</summary>
    private static void EmitMul16(Emitter e)
    {
        e.PlaceRoutine("mul16");
        e.U8(0x21); e.U8(0x00); e.U8(0x00);      // ld hl, 0
        var loop = new Label();
        var noadd = new Label();
        e.Place(loop);
        e.U8(0x78);                              // ld a, b
        e.U8(0xB1);                              // or c
        e.U8(0xC8);                              // ret z     (BC == 0 -> done)
        e.U8(0xCB); e.U8(0x38);                  // srl b
        e.U8(0xCB); e.U8(0x19);                  // rr c      (BC >>= 1, carry = old bit0)
        e.Jump(0xD2, noadd);                     // jp nc, noadd
        e.U8(0x19);                              // add hl, de
        e.Place(noadd);
        e.U8(0xCB); e.U8(0x23);                  // sla e
        e.U8(0xCB); e.U8(0x12);                  // rl d      (DE <<= 1)
        e.Jump(0xC3, loop);                      // jp loop
    }

    /// <summary>__udivmod16: DE / BC -> quotient in DE, remainder in HL (unsigned, restoring).</summary>
    private static void EmitUDivMod16(Emitter e)
    {
        e.PlaceRoutine("udivmod16");
        e.U8(0x21); e.U8(0x00); e.U8(0x00);      // ld hl, 0     (remainder)
        e.U8(0x3E); e.U8(0x10);                  // ld a, 16
        e.U8(0xEA); e.U8(RtCount & 0xFF); e.U8(RtCount >> 8); // ld (RtCount), a
        var loop = new Label();
        var dosub = new Label();
        var skip = new Label();
        e.Place(loop);
        e.U8(0xCB); e.U8(0x23);                  // sla e
        e.U8(0xCB); e.U8(0x12);                  // rl d
        e.U8(0xCB); e.U8(0x15);                  // rl l
        e.U8(0xCB); e.U8(0x14);                  // rl h    (shift HL:DE left; quotient bit in E.0)
        e.Jump(0xDA, dosub);                     // jp c, dosub   (bit16 set -> remainder >= divisor)
        e.U8(0x7D); e.U8(0x91);                  // ld a, l ; sub c
        e.U8(0x7C); e.U8(0x98);                  // ld a, h ; sbc a, b   (carry = HL < BC)
        e.Jump(0xDA, skip);                      // jp c, skip
        e.Place(dosub);
        e.U8(0x7D); e.U8(0x91); e.U8(0x6F);      // ld a, l ; sub c ; ld l, a
        e.U8(0x7C); e.U8(0x98); e.U8(0x67);      // ld a, h ; sbc a, b ; ld h, a
        e.U8(0xCB); e.U8(0xC3);                  // set 0, e   (quotient bit)
        e.Place(skip);
        e.U8(0xFA); e.U8(RtCount & 0xFF); e.U8(RtCount >> 8); // ld a, (RtCount)
        e.U8(0x3D);                              // dec a
        e.U8(0xEA); e.U8(RtCount & 0xFF); e.U8(RtCount >> 8); // ld (RtCount), a
        e.Jump(0xC2, loop);                      // jp nz, loop
        e.U8(0xC9);                              // ret
    }

    /// <summary>__sdivmod16: signed DE / BC -> quotient DE, remainder HL, via unsigned + sign fixup.</summary>
    private static void EmitSDivMod16(Emitter e)
    {
        e.PlaceRoutine("sdivmod16");
        e.U8(0x7A);                              // ld a, d
        e.U8(0xEA); e.U8(RtSignRem & 0xFF); e.U8(RtSignRem >> 8);   // ld (RtSignRem), a   (dividend sign)
        e.U8(0xA8);                              // xor b
        e.U8(0xEA); e.U8(RtSignQuot & 0xFF); e.U8(RtSignQuot >> 8); // ld (RtSignQuot), a  (sign(D)^sign(B))

        var dePos = new Label();
        e.U8(0x7A); e.U8(0xE6); e.U8(0x80);      // ld a, d ; and 0x80
        e.Jump(0xCA, dePos);                     // jp z, dePos
        NegateDE(e);
        e.Place(dePos);

        var bcPos = new Label();
        e.U8(0x78); e.U8(0xE6); e.U8(0x80);      // ld a, b ; and 0x80
        e.Jump(0xCA, bcPos);                     // jp z, bcPos
        NegateBC(e);
        e.Place(bcPos);

        e.Jump(0xCD, e.RoutineLabel("udivmod16"));  // call __udivmod16

        var qPos = new Label();
        e.U8(0xFA); e.U8(RtSignQuot & 0xFF); e.U8(RtSignQuot >> 8); e.U8(0xE6); e.U8(0x80); // ld a,(RtSignQuot); and 0x80
        e.Jump(0xCA, qPos);
        NegateDE(e);
        e.Place(qPos);

        var rPos = new Label();
        e.U8(0xFA); e.U8(RtSignRem & 0xFF); e.U8(RtSignRem >> 8); e.U8(0xE6); e.U8(0x80);   // ld a,(RtSignRem); and 0x80
        e.Jump(0xCA, rPos);
        NegateHL(e);
        e.Place(rPos);

        e.U8(0xC9);                              // ret
    }

    private static void NegateDE(Emitter e)
    {
        e.U8(0xAF); e.U8(0x93); e.U8(0x5F);      // xor a ; sub e ; ld e, a
        e.U8(0x3E); e.U8(0x00); e.U8(0x9A); e.U8(0x57); // ld a, 0 ; sbc a, d ; ld d, a
    }

    private static void NegateBC(Emitter e)
    {
        e.U8(0xAF); e.U8(0x91); e.U8(0x4F);      // xor a ; sub c ; ld c, a
        e.U8(0x3E); e.U8(0x00); e.U8(0x98); e.U8(0x47); // ld a, 0 ; sbc a, b ; ld b, a
    }

    private static void NegateHL(Emitter e)
    {
        e.U8(0xAF); e.U8(0x95); e.U8(0x6F);      // xor a ; sub l ; ld l, a
        e.U8(0x3E); e.U8(0x00); e.U8(0x9C); e.U8(0x67); // ld a, 0 ; sbc a, h ; ld h, a
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
        private readonly Dictionary<string, Label> _routines = new(StringComparer.Ordinal);
        private readonly List<(int Pos, Label Target)> _fixups = [];

        /// <summary>Runtime helper routines referenced by generated code (emitted on demand).</summary>
        public readonly HashSet<string> NeededRoutines = new(StringComparer.Ordinal);

        /// <summary>Coalesced source-line ranges over the code section, for .kdbg debug info.</summary>
        public readonly List<LineMapEntry> LineMap = [];

        public void U8(int value) => Code.Add((byte)value);

        /// <summary>Record that <paramref name="count"/> bytes at <paramref name="offset"/> came
        /// from one source line, extending the previous run when adjacent and identical.</summary>
        public void AddLineRange(int offset, int count, string file, uint line)
        {
            if (LineMap.Count > 0)
            {
                var last = LineMap[^1];
                if (last.Line == line && last.File == file && last.Offset + last.ByteCount == offset)
                {
                    LineMap[^1] = last with { ByteCount = last.ByteCount + count };
                    return;
                }
            }
            LineMap.Add(new LineMapEntry(offset, count, file, line));
        }

        public Label BlockLabel(IrBasicBlock block)
        {
            if (!_blocks.TryGetValue(block, out var label))
                _blocks[block] = label = new Label();
            return label;
        }

        public Label RoutineLabel(string name)
        {
            if (!_routines.TryGetValue(name, out var label))
                _routines[name] = label = new Label();
            NeededRoutines.Add(name);
            return label;
        }

        public void PlaceRoutine(string name) => Place(RoutineLabel(name));

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
                            // A constant index off a compile-time-known base stays a static
                            // address; anything else becomes a runtime pointer computed into a slot.
                            if (g.Index is IrConstInt ci && staticAddr.TryGetValue(g.BasePointer, out int b))
                            {
                                staticAddr[g] = b + (int)ci.Value * SizeOf(g.ElementType);
                            }
                            else
                            {
                                slot[g] = wram;
                                wram += 2;
                            }
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
    }

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

        public FunctionEmitter(
            Emitter emitter,
            IrFunction fn,
            IReadOnlyDictionary<IrFunction, FunctionAllocation> allocations,
            IReadOnlyDictionary<IrGlobal, int> globals)
        {
            _e = emitter;
            _fn = fn;
            _allocations = allocations;
            _globals = globals;
            var allocation = allocations[fn];
            _slot = allocation.Slot;
            _staticAddr = allocation.StaticAddr;
            _phiTempBase = allocation.PhiTempBase;
        }

        /// <summary>A compile-time-known address: an alloca/constant-gep, or a global's address.</summary>
        private bool TryStaticAddr(IrValue value, out int addr)
        {
            if (_staticAddr.TryGetValue(value, out addr))
                return true;
            if (value is IrGlobalRef g && _globals.TryGetValue(g.Global, out addr))
                return true;
            addr = 0;
            return false;
        }

        public void Compile()
        {
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

        /// <summary>
        /// Lower multiply/divide/remainder to the shared runtime routines. Operands are widened to
        /// 16 bits (sign-extended for signed divide/remainder, zero-extended otherwise) and passed
        /// in DE (left) and BC (right); the result comes back in DE (quotient) or HL (product /
        /// remainder) and is truncated back to the operation width.
        /// </summary>
        private void EmitMulDivRem(BinaryInstruction b)
        {
            int n = SizeOf(b.Type);
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
                    _e.U8(0x01); _e.U8(size & 0xFF); _e.U8(size >> 8); // LD BC, size
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
                _e.U8(0x21); _e.U8(addr & 0xFF); _e.U8(addr >> 8);   // LD HL, addr
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
