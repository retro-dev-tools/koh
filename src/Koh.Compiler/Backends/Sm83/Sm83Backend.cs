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
/// Supported today: <c>i8</c>/<c>i16</c>/<c>i32</c> arithmetic (add/sub via ADC/SBC byte chains,
/// and/or/xor), signed and unsigned comparisons, multiply/divide/remainder and shifts via 16-bit
/// runtime routines, integer conversions (trunc/zext/sext/bitcast), control flow (br/condbr/phi
/// with critical-edge-split phi copies), <c>switch</c>, calls (static, non-recursive), aggregate
/// (struct/array) copies, and both static-address and dynamic-pointer memory ops. Instruction
/// bytes are emitted directly rather than selected through
/// <see cref="Koh.Core.Encoding.Sm83InstructionTable"/> (that table is the encoding oracle the
/// <c>Sm83EncodingTests</c> pin the emitted bytes against).
///
/// Calling convention: parameters occupy WRAM from <see cref="WramBase"/> in declaration order;
/// an <c>i8</c> result is returned in <c>A</c>, an <c>i16</c> in <c>HL</c>, an <c>i32</c> in
/// <c>DE:HL</c> (high word <c>DE</c>, low word <c>HL</c>). Unsupported IR throws
/// <see cref="NotSupportedException"/> so the boundary stays explicit.
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
        var recursive = FindRecursiveFunctions(module);
        CheckNoInterruptReentrancy(module);

        // Assign global addresses. Initialized (or ROM-space) globals live in a fixed ROM data
        // section; RAM globals get fixed WRAM/HRAM/SRAM addresses. Function frames are placed
        // after the WRAM globals so nothing overlaps.
        var globalAddresses = new Dictionary<IrGlobal, int>(ReferenceEqualityComparer.Instance);
        var romData = new List<byte>();
        // ROM data past the fixed ROM0 window ([DataBase, 0x4000)) spills into switchable ROM banks
        // (physical banks 1, 2, … each windowed at 0x4000–0x7FFF). bankData[i] is bank (i + 1).
        var bankData = new List<List<byte>>();
        int wramGlobals = WramBase, hramGlobals = 0xFF80, sramGlobals = 0xA000;
        foreach (var g in module.Globals)
        {
            if (g.FixedAddress is int pinned)
            {
                globalAddresses[g] = pinned; // memory-mapped register / explicit placement
            }
            else if (g.AddressSpace == AddressSpace.Rom || g.Initializer is not null)
            {
                var bytes = g.Initializer ?? new byte[SizeOf(g.Type)];
                globalAddresses[g] = PlaceRomData(bytes, romData, bankData);
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

        // The software stack (for recursive frame save/restore) occupies WRAM above all static frames,
        // growing up toward the fixed runtime scratch at 0xDF00.
        int softStackBase = wram;

        var emitter = new Emitter();
        var symbols = new List<SymbolData>();

        // The cartridge boots into "main" (or the first function if there is none).
        var entryFunction = module.Functions.FirstOrDefault(f =>
                !f.IsExternal && string.Equals(f.Name, "main", StringComparison.OrdinalIgnoreCase))
            ?? module.Functions.FirstOrDefault(f => !f.IsExternal);
        var funcOffsets = new List<(IrFunction Fn, int Offset)>();
        foreach (var fn in module.Functions)
        {
            if (fn.IsExternal)
                continue;
            funcOffsets.Add((fn, emitter.Code.Count));
            new FunctionEmitter(emitter, fn, allocations, globalAddresses,
                recursive, ReferenceEquals(fn, entryFunction), softStackBase).Compile();
        }
        int funcsEnd = emitter.Code.Count;

        EmitRuntimeRoutines(emitter);

        // Code that overflows the fixed ROM0 code window ([CodeBase, DataBase)) — the trailing functions
        // and the runtime routines — moves into ROM bank 1. That is the bank MBC1 maps by default and
        // this code never switches away from it, so every call stays a direct CALL (no trampolines).
        int total = emitter.Code.Count;
        int rom0Budget = DataBase - CodeBase;
        int codeSplit = total;
        if (total > rom0Budget)
        {
            if (bankData.Count > 0)
                throw new NotSupportedException(
                    "a program cannot bank both code and read-only data (the code bank must stay mapped, "
                    + "which precludes switching to a data bank).");

            codeSplit = 0;
            foreach (var (_, offset) in funcOffsets)
                if (offset <= rom0Budget) codeSplit = offset;
            if (funcsEnd <= rom0Budget) codeSplit = funcsEnd; // all functions fit; only runtime banks

            // A single overflow bank stays mapped (no trampolines); more than one needs far-call thunks,
            // which the multi-bank path builds by re-emitting with bank-aware call routing.
            if (total - codeSplit > BankSize)
                return CompileMultiBank(module, funcOffsets, funcsEnd, allocations, globalAddresses,
                    romData, recursive, entryFunction, softStackBase, emitter.NeededRoutines);
        }

        int AddressOf(int offset) =>
            offset < codeSplit ? CodeBase + offset : BankWindow + (offset - codeSplit);

        int entryAddress = CodeBase;
        var interruptHandlers = new List<(int Vector, int Address)>();
        foreach (var (fn, offset) in funcOffsets)
        {
            int addr = AddressOf(offset);
            if (ReferenceEquals(fn, entryFunction))
                entryAddress = addr;
            if (fn.InterruptVector is int vector)
                interruptHandlers.Add((vector, addr));
            symbols.Add(new SymbolData(
                fn.Name, SymbolKind.Label, SymbolVisibility.Exported, CodeSectionName, addr));
        }

        var codeRegions = new List<(int Start, int Base)> { (0, CodeBase) };
        if (codeSplit < total)
            codeRegions.Add((codeSplit, BankWindow));
        emitter.Resolve(codeRegions);

        int extraBanks = (codeSplit < total ? 1 : 0) + bankData.Count;
        var rom0Code = emitter.Code.GetRange(0, codeSplit).ToArray();
        var rom0Lines = emitter.LineMap.Where(e => e.Offset < codeSplit).ToList();

        var sections = new List<SectionData>
        {
            new(CodeSectionName, SectionType.Rom0, fixedAddress: CodeBase, bank: 0,
                data: rom0Code, patches: Array.Empty<PatchEntry>(), lineMap: rom0Lines),
        };

        // Overflow code in bank 1 (windowed at 0x4000).
        if (codeSplit < total)
        {
            var bankCode = emitter.Code.GetRange(codeSplit, total - codeSplit).ToArray();
            var bankLines = emitter.LineMap
                .Where(e => e.Offset >= codeSplit)
                .Select(e => e with { Offset = e.Offset - codeSplit })
                .ToList();
            sections.Add(new SectionData(
                "CODEX", SectionType.RomX, fixedAddress: BankWindow, bank: 1,
                data: bankCode, patches: Array.Empty<PatchEntry>(), lineMap: bankLines));
        }

        if (romData.Count > 0)
            sections.Add(new SectionData(
                "RODATA", SectionType.Rom0, fixedAddress: DataBase, bank: 0,
                data: romData.ToArray(), patches: Array.Empty<PatchEntry>()));

        // Switchable ROM data banks (bank i+1, all windowed at 0x4000). Present only when code is not
        // banked (the two are mutually exclusive); code reads them after selecting the bank via 0x2000.
        for (int i = 0; i < bankData.Count; i++)
            sections.Add(new SectionData(
                $"ROMX_{i + 1}", SectionType.RomX, fixedAddress: BankWindow, bank: i + 1,
                data: bankData[i].ToArray(), patches: Array.Empty<PatchEntry>()));

        if (entryFunction is not null)
            sections.Add(new SectionData(
                "HEADER", SectionType.Rom0, fixedAddress: 0x0100, bank: 0,
                data: BuildHeader(entryAddress, extraBanks), patches: Array.Empty<PatchEntry>()));

        // Interrupt vectors: `jp <handler>` at 0x40/0x48/0x50/0x58/0x60.
        foreach (var (vector, address) in interruptHandlers)
            sections.Add(new SectionData(
                $"VEC_{vector:X2}", SectionType.Rom0, fixedAddress: vector, bank: 0,
                data: [0xC3, (byte)(address & 0xFF), (byte)(address >> 8)],
                patches: Array.Empty<PatchEntry>()));

        foreach (var (g, addr) in globalAddresses)
            symbols.Add(new SymbolData(
                g.Name, SymbolKind.Label, SymbolVisibility.Exported, CodeSectionName, addr));

        return new EmitModel(sections, symbols, Array.Empty<Diagnostic>());
    }

    /// <summary>
    /// Compile a program whose code needs more than one overflow bank. ROM0 holds a boot stub, the
    /// entry function, interrupt handlers, the runtime, and one far-call thunk per banked function;
    /// every other function is packed into switchable banks. A call to a banked function goes through
    /// its ROM0 thunk, which maps the callee's bank, CALLs it through the 0x4000 window, and restores
    /// the caller's bank on return (tracked in <see cref="CurBank"/>). Banked functions return via
    /// <see cref="ReturnScratch"/> so the thunk's bank restore cannot clobber the result.
    /// </summary>
    private EmitModel CompileMultiBank(
        IrModule module,
        List<(IrFunction Fn, int Offset)> funcOffsets,
        int funcsEnd,
        Dictionary<IrFunction, FunctionAllocation> allocations,
        Dictionary<IrGlobal, int> globalAddresses,
        List<byte> romData,
        HashSet<IrFunction> recursive,
        IrFunction? entryFunction,
        int softStackBase,
        HashSet<string> neededRoutines)
    {
        if (entryFunction is null)
            throw new NotSupportedException("a banked program needs an entry function.");

        // Sizes from the measurement pass.
        var order = new List<IrFunction>();
        var size = new Dictionary<IrFunction, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < funcOffsets.Count; i++)
        {
            int end = i + 1 < funcOffsets.Count ? funcOffsets[i + 1].Offset : funcsEnd;
            size[funcOffsets[i].Fn] = end - funcOffsets[i].Offset;
            order.Add(funcOffsets[i].Fn);
        }

        // Entry and interrupt handlers stay in ROM0 (the entry returns in registers; a handler must be
        // mapped for its vector). Everything else is banked, packed into 16KB banks.
        bool IsRom0(IrFunction f) => ReferenceEquals(f, entryFunction) || f.InterruptVector is not null;
        var bankedList = order.Where(f => !IsRom0(f)).ToList();
        var banked = new HashSet<IrFunction>(bankedList, ReferenceEqualityComparer.Instance);

        var bankOf = new Dictionary<IrFunction, int>(ReferenceEqualityComparer.Instance);
        int bank = 1, bankUsed = 0;
        foreach (var f in bankedList)
        {
            int s = size[f];
            if (s > BankSize)
                throw new NotSupportedException(
                    $"function '{f.Name}' is {s} bytes — larger than a 16 KB ROM bank.");
            if (bankUsed + s > BankSize) { bank++; bankUsed = 0; }
            bankOf[f] = bank;
            bankUsed += s;
        }
        int bankCount = bankedList.Count == 0 ? 0 : bank;

        var emitter = new Emitter();
        foreach (var r in neededRoutines) emitter.NeededRoutines.Add(r);
        var symbols = new List<SymbolData>();
        var funcAddr = new Dictionary<IrFunction, int>(ReferenceEqualityComparer.Instance);

        // --- ROM0 block: boot stub, entry, handlers, runtime, thunks (contiguous, physically based). ---
        // Boot: seed the current-bank shadow and jump to the entry function.
        emitter.U8(0x3E); emitter.U8(0x01);                                             // LD A, 1
        emitter.U8(0xEA); emitter.U8(CurBank & 0xFF); emitter.U8(CurBank >> 8);          // LD (CurBank), A
        emitter.U8(0xEA); emitter.U8(MbcBankSelect & 0xFF); emitter.U8(MbcBankSelect >> 8); // LD (0x2000), A
        emitter.Jump(0xC3, emitter.FunctionLabel(entryFunction));                       // JP entry

        void EmitFunc(IrFunction f, int addr)
        {
            funcAddr[f] = addr;
            new FunctionEmitter(emitter, f, allocations, globalAddresses, recursive,
                ReferenceEquals(f, entryFunction), softStackBase, banked).Compile();
        }

        foreach (var f in order.Where(IsRom0))
            EmitFunc(f, CodeBase + emitter.Code.Count);

        EmitRuntimeRoutines(emitter);

        foreach (var f in bankedList)
        {
            emitter.Place(emitter.ThunkLabel(f));
            EmitThunk(emitter, bankOf[f], emitter.FunctionLabel(f));
        }

        int rom0End = emitter.Code.Count;
        if (rom0End > DataBase - CodeBase)
            throw new NotSupportedException(
                $"ROM0 code (entry, handlers, runtime, and {bankedList.Count} far-call thunks) is "
                + $"{rom0End} bytes — more than the {DataBase - CodeBase}-byte ROM0 code window holds.");

        // --- Bank blocks: banked functions grouped by bank, windowed at 0x4000. ---
        var regions = new List<(int Start, int Base)> { (0, CodeBase) };
        var bankSpans = new List<(int Bank, int Start, int End)>();
        for (int b = 1; b <= bankCount; b++)
        {
            int start = emitter.Code.Count;
            regions.Add((start, BankWindow));
            foreach (var f in bankedList.Where(x => bankOf[x] == b))
                EmitFunc(f, BankWindow + (emitter.Code.Count - start));
            bankSpans.Add((b, start, emitter.Code.Count));
        }
        int total = emitter.Code.Count;

        emitter.Resolve(regions);

        foreach (var f in order)
            symbols.Add(new SymbolData(
                f.Name, SymbolKind.Label, SymbolVisibility.Exported, CodeSectionName, funcAddr[f]));
        foreach (var (g, addr) in globalAddresses)
            symbols.Add(new SymbolData(
                g.Name, SymbolKind.Label, SymbolVisibility.Exported, CodeSectionName, addr));

        List<LineMapEntry> LinesIn(int start, int end) => emitter.LineMap
            .Where(e => e.Offset >= start && e.Offset < end)
            .Select(e => e with { Offset = e.Offset - start })
            .ToList();

        var sections = new List<SectionData>
        {
            new(CodeSectionName, SectionType.Rom0, fixedAddress: CodeBase, bank: 0,
                data: emitter.Code.GetRange(0, rom0End).ToArray(),
                patches: Array.Empty<PatchEntry>(), lineMap: LinesIn(0, rom0End)),
        };
        foreach (var (b, start, end) in bankSpans)
            sections.Add(new SectionData(
                $"CODEX_{b}", SectionType.RomX, fixedAddress: BankWindow, bank: b,
                data: emitter.Code.GetRange(start, end - start).ToArray(),
                patches: Array.Empty<PatchEntry>(), lineMap: LinesIn(start, end)));

        if (romData.Count > 0)
            sections.Add(new SectionData(
                "RODATA", SectionType.Rom0, fixedAddress: DataBase, bank: 0,
                data: romData.ToArray(), patches: Array.Empty<PatchEntry>()));

        sections.Add(new SectionData(
            "HEADER", SectionType.Rom0, fixedAddress: 0x0100, bank: 0,
            data: BuildHeader(CodeBase, bankCount), patches: Array.Empty<PatchEntry>()));

        foreach (var f in order.Where(x => x.InterruptVector is not null))
            sections.Add(new SectionData(
                $"VEC_{f.InterruptVector:X2}", SectionType.Rom0, fixedAddress: f.InterruptVector!.Value,
                bank: 0, data: [0xC3, (byte)(funcAddr[f] & 0xFF), (byte)(funcAddr[f] >> 8)],
                patches: Array.Empty<PatchEntry>()));

        return new EmitModel(sections, symbols, Array.Empty<Diagnostic>());
    }

    /// <summary>Emit the runtime helper routines that generated code referenced. Top-level routines
    /// come first (they reference the leaf rt.* memory helpers, adding them to NeededRoutines), then
    /// the helpers, so every referenced label is placed before Resolve.</summary>
    private static void EmitRuntimeRoutines(Emitter emitter)
    {
        if (emitter.NeededRoutines.Contains("sdivmod16"))
            emitter.NeededRoutines.Add("udivmod16");
        if (emitter.NeededRoutines.Contains("mul16")) EmitMul16(emitter);
        if (emitter.NeededRoutines.Contains("udivmod16")) EmitUDivMod16(emitter);
        if (emitter.NeededRoutines.Contains("sdivmod16")) EmitSDivMod16(emitter);

        if (emitter.NeededRoutines.Contains("sdivmod_wide")) emitter.NeededRoutines.Add("udivmod_wide");
        if (emitter.NeededRoutines.Contains("mul_wide")) EmitMulWide(emitter);
        if (emitter.NeededRoutines.Contains("udivmod_wide")) EmitUDivWide(emitter);
        if (emitter.NeededRoutines.Contains("sdivmod_wide")) EmitSDivWide(emitter);
        if (emitter.NeededRoutines.Contains("shl_wide")) EmitShiftWide(emitter, "shl_wide", IrBinaryOp.Shl);
        if (emitter.NeededRoutines.Contains("lshr_wide")) EmitShiftWide(emitter, "lshr_wide", IrBinaryOp.LShr);
        if (emitter.NeededRoutines.Contains("ashr_wide")) EmitShiftWide(emitter, "ashr_wide", IrBinaryOp.AShr);
        if (emitter.NeededRoutines.Contains("rt.clracc")) EmitClrAcc(emitter);
        if (emitter.NeededRoutines.Contains("rt.rlmem")) EmitRlMem(emitter);
        if (emitter.NeededRoutines.Contains("rt.rrmem")) EmitRrMem(emitter);
        if (emitter.NeededRoutines.Contains("rt.addmem")) EmitAddMem(emitter);
        if (emitter.NeededRoutines.Contains("rt.submem")) EmitSubMem(emitter);
        if (emitter.NeededRoutines.Contains("rt.negmem")) EmitNegMem(emitter);
        if (emitter.NeededRoutines.Contains("rt.pushframe")) EmitPushFrame(emitter);
        if (emitter.NeededRoutines.Contains("rt.popframe")) EmitPopFrame(emitter);
    }

    /// <summary>Emit a ROM0 far-call thunk: save the current bank, map the callee's bank, CALL it
    /// through the 0x4000 window, then restore the caller's bank and return. The callee returns via
    /// ReturnScratch, so clobbering A here is safe.</summary>
    private static void EmitThunk(Emitter e, int bank, Label target)
    {
        e.U8(0xFA); e.U8(CurBank & 0xFF); e.U8(CurBank >> 8);              // LD A, (CurBank)
        e.U8(0xF5);                                                        // PUSH AF   (save caller bank)
        e.U8(0x3E); e.U8(bank);                                            // LD A, bank
        e.U8(0xEA); e.U8(CurBank & 0xFF); e.U8(CurBank >> 8);              // LD (CurBank), A
        e.U8(0xEA); e.U8(MbcBankSelect & 0xFF); e.U8(MbcBankSelect >> 8);  // LD (0x2000), A  (switch)
        e.Jump(0xCD, target);                                             // CALL callee (windowed)
        e.U8(0xF1);                                                        // POP AF    (caller bank)
        e.U8(0xEA); e.U8(CurBank & 0xFF); e.U8(CurBank >> 8);              // LD (CurBank), A
        e.U8(0xEA); e.U8(MbcBankSelect & 0xFF); e.U8(MbcBankSelect >> 8);  // LD (0x2000), A  (restore)
        e.U8(0xC9);                                                        // RET
    }

    /// <summary>End of the fixed ROM0 data window; data past this spills into switchable banks.</summary>
    private const int Rom0DataEnd = 0x4000;

    /// <summary>Base of the switchable ROM-bank window (0x4000–0x7FFF).</summary>
    private const int BankWindow = 0x4000;
    private const int BankSize = 0x4000;

    /// <summary>Place a read-only global's bytes into ROM0 data, or — once that 16KB window is full —
    /// into a switchable ROM bank, and return the (windowed) address the global is addressed at. A
    /// banked global's address is only valid while its bank is mapped, so code must select the bank
    /// (write it to 0x2000–0x3FFF, e.g. <c>*(byte*)0x2000 = bank;</c>) before dereferencing it.</summary>
    private static int PlaceRomData(byte[] bytes, List<byte> rom0, List<List<byte>> banks)
    {
        if (bytes.Length > BankSize)
            throw new NotSupportedException(
                $"ROM global of {bytes.Length} bytes exceeds one {BankSize}-byte ROM bank.");

        if (DataBase + rom0.Count + bytes.Length <= Rom0DataEnd)
        {
            int addr = DataBase + rom0.Count;
            rom0.AddRange(bytes);
            return addr;
        }

        // Spill into the last bank, or start a new one when the current bank cannot hold it.
        if (banks.Count == 0 || banks[^1].Count + bytes.Length > BankSize)
            banks.Add([]);
        int offset = banks[^1].Count;
        banks[^1].AddRange(bytes);
        return BankWindow + offset;
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
    /// vector, the Nintendo logo, and the cartridge fields. This makes the emitted image a bootable
    /// cartridge rather than a bare code blob. With no extra banks it is a 32 KB ROM-only cart; when
    /// data spilled into switchable banks it becomes an MBC1 of the right ROM size. The header and
    /// global checksums ($014D and $014E-F) are filled in by the linker's <c>RomWriter</c>.
    /// </summary>
    private static byte[] BuildHeader(int entryAddress, int extraBanks)
    {
        var header = new byte[0x50]; // 0x0100..0x014F

        header[0x00] = 0x00;                          // nop
        header[0x01] = 0xC3;                          // jp a16
        header[0x02] = (byte)(entryAddress & 0xFF);
        header[0x03] = (byte)(entryAddress >> 8);

        NintendoLogo.CopyTo(header.AsSpan(0x04));      // 0x0104..0x0133

        if (extraBanks > 0)
        {
            header[0x47] = 0x01;                       // cartridge type: MBC1
            // ROM size byte: 0x00=2 banks(32KB), 0x01=4(64KB), 0x02=8(128KB)… nearest power of two
            // that holds bank 0 plus the extra banks.
            int totalBanks = extraBanks + 1;
            int pow2 = 2;
            byte sizeCode = 0;
            while (pow2 < totalBanks) { pow2 <<= 1; sizeCode++; }
            header[0x48] = sizeCode;                    // ROM size
        }
        // Title bytes (0x0134..) left zero.
        return header;
    }

    /// <summary>The direct (non-external) callees of each function, for cycle detection.</summary>
    private static Dictionary<IrFunction, List<IrFunction>> BuildCalleeGraph(IrModule module)
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
        return callees;
    }

    /// <summary>The set of functions that are part of a call cycle (directly or mutually recursive).
    /// A function is recursive if it can reach itself through the call graph. Recursive functions save
    /// and restore their static frame around each entry (see <see cref="FunctionEmitter"/>), so the
    /// shared static frame survives re-entry.</summary>
    private static HashSet<IrFunction> FindRecursiveFunctions(IrModule module)
    {
        var callees = BuildCalleeGraph(module);
        var recursive = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);

        // A function is recursive iff it can reach itself. Run a reachability walk from each function.
        foreach (var fn in module.Functions)
        {
            var seen = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<IrFunction>();
            if (callees.TryGetValue(fn, out var direct))
                foreach (var c in direct)
                    stack.Push(c);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (ReferenceEquals(cur, fn)) { recursive.Add(fn); break; }
                if (!seen.Add(cur)) continue;
                if (callees.TryGetValue(cur, out var next))
                    foreach (var c in next)
                        stack.Push(c);
            }
        }
        return recursive;
    }

    /// <summary>Static WRAM frames are not reentrant. An interrupt handler can preempt main-line code
    /// at any point, so a function reachable from BOTH a handler and main-line shares one frame that the
    /// interrupt would corrupt mid-call. Reject that at compile time. A handler-only or main-only helper
    /// is safe (handlers run with interrupts disabled and never nest), so only a shared one is flagged.
    /// Must run after <see cref="CheckNoRecursion"/> so the reachability walk is acyclic.</summary>
    private static void CheckNoInterruptReentrancy(IrModule module)
    {
        var handlers = module.Functions.Where(f => f.InterruptVector is not null).ToList();
        if (handlers.Count == 0)
            return;

        var callees = BuildCalleeGraph(module);
        void Reach(IrFunction fn, HashSet<IrFunction> set)
        {
            if (!set.Add(fn))
                return;
            if (callees.TryGetValue(fn, out var next))
                foreach (var callee in next)
                    Reach(callee, set);
        }

        // Functions the interrupt handlers can call (the handlers themselves are only entered via the
        // vector, so their own frames never clash — it is their callees that may be shared).
        var handlerReach = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
        foreach (var handler in handlers)
            if (callees.TryGetValue(handler, out var next))
                foreach (var callee in next)
                    Reach(callee, handlerReach);

        // Functions main-line code can reach: roots are the non-handler functions that aren't already
        // pinned to interrupt context (a helper called only from a handler is not main-line).
        var mainReach = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
        foreach (var fn in module.Functions)
            if (fn.InterruptVector is null && !handlerReach.Contains(fn))
                Reach(fn, mainReach);

        foreach (var fn in module.Functions)
            if (handlerReach.Contains(fn) && mainReach.Contains(fn))
                throw new NotSupportedException(
                    $"function '{fn.Name}' is reachable from both an interrupt handler and main-line code; "
                    + "static WRAM frames are not reentrant, so an interrupt firing mid-call would corrupt it. "
                    + "Give the handler its own copy of the routine.");
    }

    // Fixed scratch for the (non-reentrant) runtime routines.
    private const int RtCount = 0xDF00;     // division bit counter
    private const int RtSignRem = 0xDF01;   // signed division: remainder sign
    private const int RtSignQuot = 0xDF02;  // signed division: quotient sign
    private const int RtCmpLeft = 0xDF03;   // signed compare: sign-flipped top byte of the left operand
    private const int RtCmpRight = 0xDF04;  // signed compare: sign-flipped top byte of the right operand

    // Scratch for the generic width-N (32-/64-bit) memory routines. Operand areas are 8 bytes so the
    // same code serves i32 (N=4) and i64 (N=8); N and the loop counter live in single bytes.
    private const int RtN = 0xDF08;         // operand width in bytes (4 or 8)
    private const int RtBits = 0xDF09;      // shift/division loop counter
    private const int RtOpA = 0xDF10;       // multiplicand / dividend -> quotient / shift subject
    private const int RtOpB = 0xDF18;       // multiplier / divisor
    private const int RtAcc = 0xDF20;       // product / remainder

    /// <summary>Where an i64 return value is passed: it does not fit the register file, so an i64 is
    /// returned in this fixed 8-byte scratch (little-endian). Public so tests can read it. A recursive
    /// function returns its value here at every width, so the epilogue's frame restore cannot clobber it.</summary>
    public const int ReturnScratch = 0xDF28;

    // Recursion support: recursive functions save their static frame to a software stack on entry and
    // restore it before return, receive arguments through a fixed staging area (so the caller's own
    // frame is not disturbed), and return via ReturnScratch.
    private const int SoftSp = 0xDF05;      // software-stack pointer (2 bytes)
    private const int CurBank = 0xDF07;     // currently-mapped ROM bank, for far-call thunks
    private const int ArgScratch = 0xDE80;  // recursive-call argument staging (little-endian, packed)

    /// <summary>MBC1 ROM-bank select register (a write here maps that bank into 0x4000-0x7FFF).</summary>
    private const int MbcBankSelect = 0x2000;

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

    /// <summary>Two's-complement a 16-bit register pair: <c>xor a; sub lo; ld lo,a; ld a,0; sbc a,hi; ld hi,a</c>.</summary>
    private static void NegatePair(Emitter e, int subLo, int storeLo, int sbcHi, int storeHi)
    {
        e.U8(0xAF); e.U8(subLo); e.U8(storeLo);                 // xor a ; sub lo ; ld lo, a
        e.U8(0x3E); e.U8(0x00); e.U8(sbcHi); e.U8(storeHi);     // ld a, 0 ; sbc a, hi ; ld hi, a
    }

    private static void NegateDE(Emitter e) => NegatePair(e, 0x93, 0x5F, 0x9A, 0x57); // sub e/ld e/sbc d/ld d
    private static void NegateBC(Emitter e) => NegatePair(e, 0x91, 0x4F, 0x98, 0x47); // sub c/ld c/sbc b/ld b
    private static void NegateHL(Emitter e) => NegatePair(e, 0x95, 0x6F, 0x9C, 0x67); // sub l/ld l/sbc h/ld h

    // ---- Generic width-N (32-/64-bit) memory runtime routines --------------
    //
    // These operate on little-endian byte arrays in fixed WRAM scratch: RtOpA/RtOpB are the operands
    // and RtAcc the accumulator, all N bytes wide (N in RtN). They mirror the 16-bit register routines
    // but keep the wide values in memory since the SM83 has too few registers. The SM83 ALU only reaches
    // memory through (HL), so the second operand is loaded via (DE) into a register first. Leaf memory
    // helpers (rt.*) walk with HL as the byte pointer and B as the byte counter; DEC B / INC HL / LD do
    // not touch carry, so the carry chains cleanly across bytes.

    private static void LdHL(Emitter e, int imm16) { e.U8(0x21); e.U8(imm16 & 0xFF); e.U8(imm16 >> 8); }
    private static void LdAAbs(Emitter e, int addr) { e.U8(0xFA); e.U8(addr & 0xFF); e.U8(addr >> 8); }
    private static void StAAbs(Emitter e, int addr) { e.U8(0xEA); e.U8(addr & 0xFF); e.U8(addr >> 8); }
    private static void LdBFromN(Emitter e) { LdAAbs(e, RtN); e.U8(0x47); }   // A=(RtN); LD B,A

    /// <summary>HL += (RtN - 1), so a pointer at an operand's low byte moves to its high byte.</summary>
    private static void AdvanceHLToMsb(Emitter e)
    {
        LdAAbs(e, RtN); e.U8(0x3D);                      // ld a,(RtN) ; dec a   -> A = N-1
        e.U8(0x85); e.U8(0x6F);                          // add a,l ; ld l,a
        e.U8(0x3E); e.U8(0x00); e.U8(0x8C); e.U8(0x67);  // ld a,0 ; adc a,h ; ld h,a
    }

    /// <summary>rt.clracc: zero the N bytes at RtAcc.</summary>
    private static void EmitClrAcc(Emitter e)
    {
        e.PlaceRoutine("rt.clracc");
        LdBFromN(e);                             // B = N
        LdHL(e, RtAcc);                          // HL = RtAcc
        e.U8(0xAF);                              // xor a  (A = 0)
        var loop = new Label();
        e.Place(loop);
        e.U8(0x22);                              // ld (hl+), a
        e.U8(0x05);                              // dec b
        e.Jump(0xC2, loop);                      // jp nz, loop
        e.U8(0xC9);                              // ret
    }

    /// <summary>rt.rlmem: rotate the N-byte value at HL left through carry (carry-in preserved). LSB first.</summary>
    private static void EmitRlMem(Emitter e)
    {
        e.PlaceRoutine("rt.rlmem");
        var loop = new Label();
        e.Place(loop);
        e.U8(0xCB); e.U8(0x16);                  // rl (hl)
        e.U8(0x23);                              // inc hl
        e.U8(0x05);                              // dec b
        e.Jump(0xC2, loop);                      // jp nz, loop
        e.U8(0xC9);                              // ret
    }

    /// <summary>rt.rrmem: rotate the N-byte value at HL right through carry (carry-in preserved). MSB first
    /// (HL points at the high byte and walks down).</summary>
    private static void EmitRrMem(Emitter e)
    {
        e.PlaceRoutine("rt.rrmem");
        var loop = new Label();
        e.Place(loop);
        e.U8(0xCB); e.U8(0x1E);                  // rr (hl)
        e.U8(0x2B);                              // dec hl
        e.U8(0x05);                              // dec b
        e.Jump(0xC2, loop);                      // jp nz, loop
        e.U8(0xC9);                              // ret
    }

    /// <summary>rt.addmem: (HL..) += (DE..) over B bytes; carry cleared first. Result carry = final carry-out.</summary>
    private static void EmitAddMem(Emitter e)
    {
        e.PlaceRoutine("rt.addmem");
        e.U8(0xAF);                              // xor a  (clear carry)
        var loop = new Label();
        e.Place(loop);
        e.U8(0x1A);                              // ld a, (de)   src byte
        e.U8(0x8E);                              // adc a, (hl)  src + dst + carry
        e.U8(0x77);                              // ld (hl), a
        e.U8(0x23);                              // inc hl
        e.U8(0x13);                              // inc de
        e.U8(0x05);                              // dec b
        e.Jump(0xC2, loop);                      // jp nz, loop
        e.U8(0xC9);                              // ret
    }

    /// <summary>rt.submem: (HL..) -= (DE..) over B bytes; borrow cleared first. Result carry = final borrow.</summary>
    private static void EmitSubMem(Emitter e)
    {
        e.PlaceRoutine("rt.submem");
        e.U8(0xA7);                              // and a  (clear carry/borrow)
        var loop = new Label();
        e.Place(loop);
        e.U8(0x1A);                              // ld a, (de)   src byte
        e.U8(0x4F);                              // ld c, a
        e.U8(0x7E);                              // ld a, (hl)   dst byte
        e.U8(0x99);                              // sbc a, c     dst - src - borrow
        e.U8(0x77);                              // ld (hl), a
        e.U8(0x23);                              // inc hl
        e.U8(0x13);                              // inc de
        e.U8(0x05);                              // dec b
        e.Jump(0xC2, loop);                      // jp nz, loop
        e.U8(0xC9);                              // ret
    }

    /// <summary>rt.negmem: two's-complement the N-byte value at HL (over B bytes). LSB first.</summary>
    private static void EmitNegMem(Emitter e)
    {
        e.PlaceRoutine("rt.negmem");
        e.U8(0xA7);                              // and a  (clear borrow)
        var loop = new Label();
        e.Place(loop);
        e.U8(0x3E); e.U8(0x00);                  // ld a, 0   (flags untouched)
        e.U8(0x9E);                              // sbc a, (hl)   0 - byte - borrow
        e.U8(0x77);                              // ld (hl), a
        e.U8(0x23);                              // inc hl
        e.U8(0x05);                              // dec b
        e.Jump(0xC2, loop);                      // jp nz, loop
        e.U8(0xC9);                              // ret
    }

    /// <summary>rt.pushframe: save B bytes at (DE) to the software stack, advancing SoftSp. Used by a
    /// recursive function's prologue to preserve the caller's copy of the shared static frame.</summary>
    private static void EmitPushFrame(Emitter e)
    {
        e.PlaceRoutine("rt.pushframe");
        LdAAbs(e, SoftSp); e.U8(0x6F);                   // ld a,(SoftSp)   ; ld l,a
        LdAAbs(e, SoftSp + 1); e.U8(0x67);               // ld a,(SoftSp+1) ; ld h,a   -> HL = SoftSp
        var loop = new Label();
        e.Place(loop);
        e.U8(0x1A);                                      // ld a,(de)
        e.U8(0x22);                                      // ld (hl+),a
        e.U8(0x13);                                      // inc de
        e.U8(0x05);                                      // dec b
        e.Jump(0xC2, loop);                              // jp nz, loop
        e.U8(0x7D); StAAbs(e, SoftSp);                   // ld a,l ; ld (SoftSp),a
        e.U8(0x7C); StAAbs(e, SoftSp + 1);               // ld a,h ; ld (SoftSp+1),a
        e.U8(0xC9);                                      // ret
    }

    /// <summary>rt.popframe: retreat SoftSp by B, then restore B bytes from the software stack to (DE).
    /// Used by a recursive function's epilogue to put the caller's frame back before returning.</summary>
    private static void EmitPopFrame(Emitter e)
    {
        e.PlaceRoutine("rt.popframe");
        LdAAbs(e, SoftSp); e.U8(0x90); e.U8(0x6F);       // ld a,(SoftSp) ; sub b ; ld l,a
        LdAAbs(e, SoftSp + 1); e.U8(0xDE); e.U8(0x00); e.U8(0x67); // ld a,(SoftSp+1) ; sbc a,0 ; ld h,a -> HL = SoftSp-B
        e.U8(0x7D); StAAbs(e, SoftSp);                   // ld a,l ; ld (SoftSp),a
        e.U8(0x7C); StAAbs(e, SoftSp + 1);               // ld a,h ; ld (SoftSp+1),a
        var loop = new Label();
        e.Place(loop);
        e.U8(0x2A);                                      // ld a,(hl+)
        e.U8(0x12);                                      // ld (de),a
        e.U8(0x13);                                      // inc de
        e.U8(0x05);                                      // dec b
        e.Jump(0xC2, loop);                              // jp nz, loop
        e.U8(0xC9);                                      // ret
    }

    /// <summary>Load RtBits with N*8 (the full-width bit count).</summary>
    private static void LoadFullBitCount(Emitter e)
    {
        LdAAbs(e, RtN);
        e.U8(0x87); e.U8(0x87); e.U8(0x87);      // add a,a x3  -> A = N*8
        StAAbs(e, RtBits);
    }

    /// <summary>Shift RtOpA left by one bit (N bytes, shifting in 0).</summary>
    private static void ShlOpAOnce(Emitter e)
    {
        LdBFromN(e);
        LdHL(e, RtOpA);
        e.U8(0xAF);                              // xor a  (clear carry -> shift in 0)
        e.U8(0xCD); AddRoutineCall(e, "rt.rlmem");
    }

    /// <summary>Shift RtOpB right by one bit, logical (N bytes, shifting in 0).</summary>
    private static void ShrOpBOnce(Emitter e)
    {
        LdBFromN(e);
        LdHL(e, RtOpB);
        AdvanceHLToMsb(e);
        e.U8(0xAF);                              // xor a  (clear carry -> logical)
        e.U8(0xCD); AddRoutineCall(e, "rt.rrmem");
    }

    /// <summary>Emit the two-byte target of a CALL (0xCD already emitted) to a runtime routine.</summary>
    private static void AddRoutineCall(Emitter e, string routine) => e.CallTarget(e.RoutineLabel(routine));

    /// <summary>rt.mul_wide: RtAcc = RtOpA * RtOpB (low N bytes) by shift-and-add.</summary>
    private static void EmitMulWide(Emitter e)
    {
        e.PlaceRoutine("mul_wide");
        e.U8(0xCD); AddRoutineCall(e, "rt.clracc");   // RtAcc = 0
        LoadFullBitCount(e);                          // RtBits = N*8
        var loop = new Label();
        var noadd = new Label();
        e.Place(loop);
        LdAAbs(e, RtOpB);                             // A = multiplier low byte
        e.U8(0x0F);                                   // rrca  -> bit0 into carry
        e.Jump(0xD2, noadd);                          // jp nc, noadd
        LdBFromN(e);                                  // B = N
        LdHL(e, RtAcc);                               // HL = RtAcc
        e.U8(0x11); e.U8(RtOpA & 0xFF); e.U8(RtOpA >> 8); // ld de, RtOpA
        e.U8(0xCD); AddRoutineCall(e, "rt.addmem");   // RtAcc += RtOpA
        e.Place(noadd);
        ShlOpAOnce(e);                                // RtOpA <<= 1
        ShrOpBOnce(e);                                // RtOpB >>= 1
        LdAAbs(e, RtBits); e.U8(0x3D); StAAbs(e, RtBits); // dec RtBits
        e.Jump(0xC2, loop);                           // jp nz, loop
        e.U8(0xC9);                                   // ret
    }

    /// <summary>rt.udivmod_wide: RtOpA / RtOpB -> quotient in RtOpA, remainder in RtAcc (unsigned, restoring).</summary>
    private static void EmitUDivWide(Emitter e)
    {
        e.PlaceRoutine("udivmod_wide");
        e.U8(0xCD); AddRoutineCall(e, "rt.clracc");   // remainder RtAcc = 0
        LoadFullBitCount(e);                          // RtBits = N*8
        var loop = new Label();
        var restore = new Label();
        var next = new Label();
        e.Place(loop);
        // Shift {RtAcc:RtOpA} left by one: RtOpA's high bit flows into RtAcc's low bit.
        LdBFromN(e); LdHL(e, RtOpA); e.U8(0xAF);      // B=N; HL=RtOpA; clear carry
        e.U8(0xCD); AddRoutineCall(e, "rt.rlmem");    // RtOpA <<= 1, carry = old MSB
        LdBFromN(e); LdHL(e, RtAcc);                  // B=N; HL=RtAcc (carry preserved)
        e.U8(0xCD); AddRoutineCall(e, "rt.rlmem");    // RtAcc <<= 1 with carry-in
        // Try remainder -= divisor.
        LdBFromN(e); LdHL(e, RtAcc);
        e.U8(0x11); e.U8(RtOpB & 0xFF); e.U8(RtOpB >> 8); // ld de, RtOpB
        e.U8(0xCD); AddRoutineCall(e, "rt.submem");   // RtAcc -= RtOpB, carry = borrow
        e.Jump(0xDA, restore);                        // jp c, restore   (remainder < divisor)
        LdAAbs(e, RtOpA); e.U8(0xF6); e.U8(0x01); StAAbs(e, RtOpA); // set quotient bit0
        e.Jump(0xC3, next);                           // jp next
        e.Place(restore);
        LdBFromN(e); LdHL(e, RtAcc);
        e.U8(0x11); e.U8(RtOpB & 0xFF); e.U8(RtOpB >> 8); // ld de, RtOpB
        e.U8(0xCD); AddRoutineCall(e, "rt.addmem");   // restore remainder
        e.Place(next);
        LdAAbs(e, RtBits); e.U8(0x3D); StAAbs(e, RtBits);
        e.Jump(0xC2, loop);
        e.U8(0xC9);                                   // ret
    }

    /// <summary>rt.sdivmod_wide: signed RtOpA / RtOpB via the unsigned routine plus sign fixup.</summary>
    private static void EmitSDivWide(Emitter e)
    {
        e.PlaceRoutine("sdivmod_wide");
        // Record signs: remainder takes the dividend's sign; quotient takes sign(dividend) ^ sign(divisor).
        LdHL(e, RtOpA); AdvanceHLToMsb(e); e.U8(0x7E); StAAbs(e, RtSignRem); // A = dividend MSB
        e.U8(0x47);                                   // ld b, a  (save dividend sign byte)
        LdHL(e, RtOpB); AdvanceHLToMsb(e); e.U8(0x7E); // A = divisor MSB
        e.U8(0xA8);                                   // xor b
        StAAbs(e, RtSignQuot);
        // Negate negative operands.
        var aPos = new Label();
        LdAAbs(e, RtSignRem); e.U8(0xE6); e.U8(0x80); // and 0x80
        e.Jump(0xCA, aPos);
        LdBFromN(e); LdHL(e, RtOpA); e.U8(0xCD); AddRoutineCall(e, "rt.negmem");
        e.Place(aPos);
        var bPos = new Label();
        LdHL(e, RtOpB); AdvanceHLToMsb(e); e.U8(0x7E); e.U8(0xE6); e.U8(0x80); // divisor MSB & 0x80
        e.Jump(0xCA, bPos);
        LdBFromN(e); LdHL(e, RtOpB); e.U8(0xCD); AddRoutineCall(e, "rt.negmem");
        e.Place(bPos);
        e.U8(0xCD); AddRoutineCall(e, "udivmod_wide");
        // Fix result signs.
        var qPos = new Label();
        LdAAbs(e, RtSignQuot); e.U8(0xE6); e.U8(0x80);
        e.Jump(0xCA, qPos);
        LdBFromN(e); LdHL(e, RtOpA); e.U8(0xCD); AddRoutineCall(e, "rt.negmem");
        e.Place(qPos);
        var rPos = new Label();
        LdAAbs(e, RtSignRem); e.U8(0xE6); e.U8(0x80);
        e.Jump(0xCA, rPos);
        LdBFromN(e); LdHL(e, RtAcc); e.U8(0xCD); AddRoutineCall(e, "rt.negmem");
        e.Place(rPos);
        e.U8(0xC9);                                   // ret
    }

    /// <summary>rt.shl_wide / rt.lshr_wide / rt.ashr_wide: shift RtOpA by RtBits bits (already clamped).</summary>
    private static void EmitShiftWide(Emitter e, string routine, IrBinaryOp op)
    {
        e.PlaceRoutine(routine);
        var loop = new Label();
        e.Place(loop);
        LdAAbs(e, RtBits); e.U8(0xA7);                // ld a,(RtBits) ; and a
        e.U8(0xC8);                                   // ret z  (count exhausted)
        e.U8(0x3D); StAAbs(e, RtBits);                // dec a ; store
        if (op == IrBinaryOp.Shl)
        {
            LdBFromN(e); LdHL(e, RtOpA); e.U8(0xAF);  // B=N; HL=RtOpA; clear carry
            e.U8(0xCD); AddRoutineCall(e, "rt.rlmem");
        }
        else
        {
            LdBFromN(e); LdHL(e, RtOpA); AdvanceHLToMsb(e); // B=N; HL -> RtOpA MSB
            if (op == IrBinaryOp.AShr)
            {
                e.U8(0x7E); e.U8(0x07);               // ld a,(hl) ; rlca  -> sign bit into carry
            }
            else
            {
                e.U8(0xAF);                           // xor a  (logical: shift in 0)
            }
            e.U8(0xCD); AddRoutineCall(e, "rt.rrmem");
        }
        e.Jump(0xC3, loop);                           // jp loop
    }

    internal static int SizeOf(IrType type) => type.SizeInBytes;

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

        // Accumulator tracking for redundant-load elimination: the slot address A currently mirrors,
        // valid only while nothing has been emitted since (Code.Count unchanged). Any other emit
        // advances Code.Count and any label/branch clears it, so a skip is only ever taken when A
        // provably still holds the value.
        private int _aSlot = -1;
        private int _aValidCount = -1;

        public void U8(int value) => Code.Add((byte)value);

        /// <summary>Emit <c>LD A, (addr)</c>, unless A already holds that slot's value.</summary>
        public void LoadA(int addr)
        {
            if (_aSlot == addr && _aValidCount == Code.Count)
                return; // A already mirrors (addr); nothing ran since it was set
            Code.Add(0xFA);
            Code.Add((byte)(addr & 0xFF));
            Code.Add((byte)(addr >> 8));
            _aSlot = addr;
            _aValidCount = Code.Count;
        }

        /// <summary>Emit <c>LD (addr), A</c>; A now also mirrors that slot.</summary>
        public void StoreA(int addr)
        {
            Code.Add(0xEA);
            Code.Add((byte)(addr & 0xFF));
            Code.Add((byte)(addr >> 8));
            _aSlot = addr;
            _aValidCount = Code.Count;
        }

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

        private readonly Dictionary<IrFunction, Label> _funcs = new(ReferenceEqualityComparer.Instance);

        /// <summary>The label at a function's true entry (before any prologue), which is what a CALL
        /// targets — distinct from the entry basic block's label, which a recursive prologue precedes.</summary>
        public Label FunctionLabel(IrFunction fn)
        {
            if (!_funcs.TryGetValue(fn, out var label))
                _funcs[fn] = label = new Label();
            return label;
        }

        public Label RoutineLabel(string name)
        {
            if (!_routines.TryGetValue(name, out var label))
                _routines[name] = label = new Label();
            NeededRoutines.Add(name);
            return label;
        }

        private readonly Dictionary<IrFunction, Label> _thunks = new(ReferenceEqualityComparer.Instance);

        /// <summary>The ROM0 far-call thunk for a banked function: it switches to the callee's bank,
        /// CALLs it through the 0x4000 window, and restores the caller's bank on return.</summary>
        public Label ThunkLabel(IrFunction fn)
        {
            if (!_thunks.TryGetValue(fn, out var label))
                _thunks[fn] = label = new Label();
            return label;
        }

        public void PlaceRoutine(string name) => Place(RoutineLabel(name));

        public void Place(Label label)
        {
            label.Offset = Code.Count;
            _aValidCount = -1; // a label is a control-flow join; A's contents are unknown here
        }

        /// <summary>Emit a jump opcode plus a two-byte placeholder patched to the label's address.</summary>
        public void Jump(int opcode, Label target)
        {
            Code.Add((byte)opcode);
            CallTarget(target);
        }

        /// <summary>Emit a two-byte address placeholder (for a CALL/JP whose opcode was already emitted),
        /// patched to the label's absolute address at resolve time.</summary>
        public void CallTarget(Label target)
        {
            _fixups.Add((Code.Count, target));
            Code.Add(0);
            Code.Add(0);
            _aValidCount = -1; // control leaves; do not carry A across the branch
        }

        /// <summary>Patch every fixup to its target's absolute address. The buffer is partitioned into
        /// contiguous regions given as (bufferStart, base) pairs sorted by start; an offset in a region
        /// resolves to <c>base + (offset - bufferStart)</c>. ROM0 uses its physical base; each banked
        /// region uses the 0x4000 window base.</summary>
        public void Resolve(IReadOnlyList<(int Start, int Base)> regions)
        {
            int RegionBaseFor(int offset)
            {
                int best = regions[0].Base, bestStart = regions[0].Start;
                foreach (var (start, baseAddr) in regions)
                    if (start <= offset && start >= bestStart) { best = baseAddr; bestStart = start; }
                return best + (offset - bestStart);
            }

            foreach (var (pos, target) in _fixups)
            {
                if (target.Offset < 0)
                    throw new InvalidOperationException("unplaced jump target");
                int addr = RegionBaseFor(target.Offset);
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
    internal sealed class FunctionAllocation
    {
        private static readonly ReferenceEqualityComparer Eq = ReferenceEqualityComparer.Instance;

        public required Dictionary<IrValue, int> Slot { get; init; }
        public required Dictionary<IrValue, int> StaticAddr { get; init; }
        public required int PhiTempBase { get; init; }
        public required int FrameBase { get; init; }
        public required int FrameEnd { get; init; }

        public static FunctionAllocation For(IrFunction fn, int baseAddr)
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
                wram += SizeOf(p.Type);
            }

            // Permanent storage: alloca objects and constant-index geps (address-taken).
            foreach (var block in fn.Blocks)
                foreach (var instr in block.Instructions)
                {
                    if (instr is PhiInstruction)
                        phiTempBytes += SizeOf(instr.Type); // cycle-breaking may stage one temp per phi
                    switch (instr)
                    {
                        case AllocaInstruction a:
                            staticAddr[a] = wram;
                            wram += SizeOf(a.Allocated);
                            break;
                        case GetElementPtrInstruction g
                            when g.Index is IrConstInt ci && staticAddr.TryGetValue(g.BasePointer, out int b):
                            staticAddr[g] = b + (int)ci.Value * SizeOf(g.ElementType);
                            break;
                    }
                }

            // Colour the remaining SSA value slots (non-void results that aren't static addresses):
            // values whose live ranges never overlap share WRAM bytes.
            var colored = new HashSet<IrValue>(Eq);
            foreach (var block in fn.Blocks)
                foreach (var instr in block.Instructions)
                    if (instr.Type.Kind != IrTypeKind.Void && instr is not AllocaInstruction
                        && !staticAddr.ContainsKey(instr))
                        colored.Add(instr);

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
                int size = SizeOf(value.Type);
                int start = colorBase;
                bool placed = false;
                while (!placed)
                {
                    placed = true;
                    foreach (var neighbour in interference[value])
                        if (slot.TryGetValue(neighbour, out int ns)
                            && Overlaps(start, size, ns, SizeOf(neighbour.Type)))
                        {
                            start = ns + SizeOf(neighbour.Type); // move past the conflict, then recheck
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
                PhiTempBase = phiTempBase,
                FrameBase = baseAddr,
                FrameEnd = wram,
            };
        }

        private static bool Overlaps(int s1, int z1, int s2, int z2) => s1 < s2 + z2 && s2 < s1 + z1;

        /// <summary>
        /// Live-range interference over the coloured values via SSA liveness (phi operands are used
        /// on predecessor edges). Two values interfere if they can be simultaneously live.
        /// </summary>
        private static Dictionary<IrValue, HashSet<IrValue>> ComputeInterference(
            IrFunction fn, HashSet<IrValue> colored)
        {
            var blocks = fn.Blocks;
            var use = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
            var def = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
            var phis = new Dictionary<IrBasicBlock, List<PhiInstruction>>(Eq);

            foreach (var b in blocks)
            {
                var u = new HashSet<IrValue>(Eq);
                var d = new HashSet<IrValue>(Eq);
                var blockPhis = new List<PhiInstruction>();
                foreach (var instr in b.Instructions)
                    if (instr is PhiInstruction phi && colored.Contains(phi))
                    {
                        d.Add(phi);           // phis define at the top
                        blockPhis.Add(phi);
                    }
                var defined = new HashSet<IrValue>(d, Eq);
                foreach (var instr in b.Instructions)
                {
                    if (instr is PhiInstruction)
                        continue;
                    foreach (var op in instr.Operands)
                        if (colored.Contains(op) && !defined.Contains(op))
                            u.Add(op);        // used before defined in this block
                    if (colored.Contains(instr))
                    {
                        d.Add(instr);
                        defined.Add(instr);
                    }
                }
                use[b] = u;
                def[b] = d;
                phis[b] = blockPhis;
            }

            var liveIn = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
            var liveOut = new Dictionary<IrBasicBlock, HashSet<IrValue>>(Eq);
            foreach (var b in blocks)
            {
                liveIn[b] = new HashSet<IrValue>(Eq);
                liveOut[b] = new HashSet<IrValue>(Eq);
            }
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = blocks.Count - 1; i >= 0; i--)
                {
                    var b = blocks[i];
                    var newOut = new HashSet<IrValue>(Eq);
                    foreach (var s in Successors(b))
                    {
                        foreach (var v in liveIn[s])
                            newOut.Add(v);
                        foreach (var phi in phis[s])                    // phi operands: live-out of the matching predecessor
                            foreach (var (val, pred) in phi.Incomings)
                                if (ReferenceEquals(pred, b) && colored.Contains(val))
                                    newOut.Add(val);
                    }
                    var newIn = new HashSet<IrValue>(use[b], Eq);
                    foreach (var v in newOut)
                        if (!def[b].Contains(v))
                            newIn.Add(v);
                    if (!newOut.SetEquals(liveOut[b]) || !newIn.SetEquals(liveIn[b]))
                    {
                        liveOut[b] = newOut;
                        liveIn[b] = newIn;
                        changed = true;
                    }
                }
            }

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

            foreach (var b in blocks)
            {
                var live = new HashSet<IrValue>(liveOut[b], Eq);
                for (int i = b.Instructions.Count - 1; i >= 0; i--)
                {
                    var instr = b.Instructions[i];
                    if (instr is PhiInstruction)
                        continue;
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
                    bool wideResult = colored.Contains(instr) && SizeOf(instr.Type) >= 2;
                    foreach (var op in instr.Operands)
                        if (colored.Contains(op))
                        {
                            if (wideResult)
                                Interfere(instr, op);
                            live.Add(op);
                        }
                }
                var blockPhis = phis[b];
                foreach (var p in blockPhis)
                    foreach (var w in live)
                        Interfere(p, w);
                for (int i = 0; i < blockPhis.Count; i++)
                    for (int j = i + 1; j < blockPhis.Count; j++)
                        Interfere(blockPhis[i], blockPhis[j]);
            }

            return graph;
        }

        private static IEnumerable<IrBasicBlock> Successors(IrBasicBlock block) =>
            block.Terminator?.Successors ?? [];
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
                // Initialize the software-stack pointer once at boot (only needed when some function
                // recurses and therefore saves its frame there).
                _e.U8(0x21); _e.U8(_softStackBase & 0xFF); _e.U8(_softStackBase >> 8); // LD HL, softStackBase
                _e.U8(0x7D); _e.StoreA(SoftSp);       // LD A, L ; LD (SoftSp), A
                _e.U8(0x7C); _e.StoreA(SoftSp + 1);   // LD A, H ; LD (SoftSp+1), A
            }

            if (IsRecursive)
            {
                if (_frameSize > 255)
                    throw new NotSupportedException(
                        $"recursive function '{_fn.Name}' frame is {_frameSize} bytes; the software-stack "
                        + "save supports up to 255.");
                // Save the caller's copy of the shared static frame, then install this call's arguments
                // (staged in ArgScratch) into the parameter slots at the frame base.
                _e.U8(0x11); _e.U8(_frameBase & 0xFF); _e.U8(_frameBase >> 8); // LD DE, frameBase
                _e.U8(0x06); _e.U8(_frameSize);                                // LD B, frameSize
                _e.Jump(0xCD, _e.RoutineLabel("rt.pushframe"));
                int paramBytes = ParamBytes(_fn);
                for (int k = 0; k < paramBytes; k++)
                {
                    _e.LoadA(ArgScratch + k);
                    _e.StoreA(_frameBase + k);
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
            if (UsesMemoryReturn(_fn))
            {
                // Return via ReturnScratch (memory) so neither the recursive frame restore nor the far-call
                // thunk's bank restore can clobber it. A recursive function also restores its frame here.
                if (r.Value is not null)
                {
                    int n = SizeOf(r.Value.Type);
                    for (int k = 0; k < n; k++)
                    {
                        LoadByteToA(r.Value, k);
                        StoreAToAddr(ReturnScratch + k);
                    }
                }
                if (IsRecursive)
                {
                    _e.U8(0x11); _e.U8(_frameBase & 0xFF); _e.U8(_frameBase >> 8); // LD DE, frameBase
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
                        // i64 has no register room; return it in the fixed ReturnScratch (little-endian).
                        for (int k = 0; k < 8; k++)
                        {
                            LoadByteToA(r.Value, k);
                            StoreAToAddr(ReturnScratch + k);
                        }
                        break;
                    default:
                        throw new NotSupportedException(
                            $"SM83 backend can only return i8 (A), i16 (HL), i32 (DE:HL), or i64 (memory), "
                            + $"not {r.Value.Type}.");
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
                    for (int k = 0; k < n; k++)
                    {
                        LoadByteToA(call.Arguments[i], k);
                        StoreAToAddr(ArgScratch + off + k);
                    }
                    off += n;
                }
            }
            else
            {
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
                int rn = SizeOf(call.Type);
                for (int k = 0; k < rn; k++)
                {
                    LoadAFromAddr(ReturnScratch + k);
                    StoreAToAddr(dst + k);
                }
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
                    for (int k = 0; k < 8; k++)            // i64 comes back in ReturnScratch
                    {
                        LoadAFromAddr(ReturnScratch + k);
                        StoreAToAddr(dst + k);
                    }
                    break;
                default:
                    throw new NotSupportedException(
                        $"SM83 backend can only capture i8/i16/i32/i64 return values, not {call.Type}.");
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
