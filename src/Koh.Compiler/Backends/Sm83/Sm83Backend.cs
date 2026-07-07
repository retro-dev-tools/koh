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
public sealed partial class Sm83Backend : IBackend
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

    /// <summary>
    /// A size/capacity limit of the target that a user program can hit (e.g. a function too large
    /// for a ROM bank, a recursive frame past the software-stack limit). Unlike an internal
    /// <see cref="NotSupportedException"/> — which signals a compiler bug or out-of-subset IR — this
    /// is driven by legal input, so <see cref="Compile"/> catches it and reports a diagnostic rather
    /// than letting it escape the driver.
    /// </summary>
    private sealed class Sm83LimitException(string message) : Exception(message);

    public EmitModel Compile(IrModule module, DiagnosticBag diagnostics)
    {
        try
        {
            return CompileCore(module, diagnostics);
        }
        catch (Sm83LimitException ex)
        {
            diagnostics.Report(new Koh.Core.Syntax.TextSpan(0, 0), ex.Message);
            return new EmitModel(
                Array.Empty<SectionData>(), Array.Empty<SymbolData>(), diagnostics.ToArray());
        }
    }

    private EmitModel CompileCore(IrModule module, DiagnosticBag diagnostics)
    {
        var recursive = FindRecursiveFunctions(module);
        CheckNoInterruptReentrancy(module);

        // A recursive value returns through ReturnScratch and the frame save/restore path emits a plain
        // RET; an interrupt handler must instead end in RETI after restoring the registers its prologue
        // pushed. The two epilogues are incompatible, so a recursive handler is rejected rather than
        // silently emitted with the wrong return.
        foreach (var fn in module.Functions)
            if (fn.InterruptVector is not null && recursive.Contains(fn))
                throw new Sm83LimitException(
                    $"interrupt handler '{fn.Name}' is recursive; an interrupt handler cannot recurse "
                    + "(its epilogue must be RETI with a balanced stack).");

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
                throw new Sm83LimitException(
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
                throw new Sm83LimitException(
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
        SelectBank(emitter);                                                            // seed current-bank shadow + MBC1
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
            throw new Sm83LimitException(
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

            // Packing used sizes measured in the (non-banked) first pass; a banked function's real
            // emission is larger (it copies its result out to ReturnScratch), so a bank that packed near
            // full could exceed the 16KB window here. Reject that rather than emit an overflowing section.
            if (emitter.Code.Count - start > BankSize)
                throw new Sm83LimitException(
                    $"ROM bank {b} holds {emitter.Code.Count - start} bytes of banked code — more than the "
                    + $"{BankSize}-byte bank window. Split the banked functions across more/smaller units.");
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

    // A composite routine that requires another routine be present even if it never CALLs it directly
    // through RoutineLabel (signed div/rem reuses the unsigned core after adjusting signs).
    private static readonly (string Routine, string Requires)[] RuntimePrereqs =
    [
        ("sdivmod16", "udivmod16"),
        ("sdivmod_wide", "udivmod_wide"),
    ];

    // Every runtime routine and its emitter, in placement order. Composite routines precede the leaf
    // rt.* helpers they reference so the common case places everything in one pass.
    private static readonly (string Name, Action<Emitter> Emit)[] RuntimeEmitters =
    [
        ("mul16", EmitMul16), ("udivmod16", EmitUDivMod16), ("sdivmod16", EmitSDivMod16),
        ("mul_wide", EmitMulWide), ("udivmod_wide", EmitUDivWide), ("sdivmod_wide", EmitSDivWide),
        ("shl_wide", e => EmitShiftWide(e, "shl_wide", IrBinaryOp.Shl)),
        ("lshr_wide", e => EmitShiftWide(e, "lshr_wide", IrBinaryOp.LShr)),
        ("ashr_wide", e => EmitShiftWide(e, "ashr_wide", IrBinaryOp.AShr)),
        ("rt.clracc", EmitClrAcc), ("rt.rlmem", EmitRlMem), ("rt.rrmem", EmitRrMem),
        ("rt.addmem", EmitAddMem), ("rt.submem", EmitSubMem), ("rt.negmem", EmitNegMem),
        ("rt.pushframe", EmitPushFrame), ("rt.popframe", EmitPopFrame),
    ];

    /// <summary>Emit the runtime helper routines that generated code referenced. Emitting a routine can
    /// add more <c>NeededRoutines</c> (the leaf rt.* helpers it CALLs via <c>RoutineLabel</c>), so this
    /// runs to a fixpoint: a routine pulled in after its table position is picked up on the next sweep.
    /// Ordering therefore can't silently leave a referenced label unplaced for <c>Resolve</c>.</summary>
    private static void EmitRuntimeRoutines(Emitter emitter)
    {
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (var (routine, requires) in RuntimePrereqs)
                if (emitter.NeededRoutines.Contains(routine))
                    emitter.NeededRoutines.Add(requires);
            foreach (var (name, emit) in RuntimeEmitters)
                if (emitter.NeededRoutines.Contains(name) && emitted.Add(name))
                {
                    emit(emitter);
                    progress = true; // emitting may have appended new leaf routines to NeededRoutines
                }
        }
    }

    /// <summary>Given the bank number in <c>A</c>, make it current: write it to both the CurBank shadow
    /// and the MBC1 bank-select register (0x2000).</summary>
    private static void SelectBank(Emitter e)
    {
        StAAbs(e, CurBank);        // LD (CurBank), A
        StAAbs(e, MbcBankSelect);  // LD (0x2000), A  (MBC1 switches on this write)
    }

    /// <summary>Emit a ROM0 far-call thunk: save the current bank, map the callee's bank, CALL it
    /// through the 0x4000 window, then restore the caller's bank and return. The callee returns via
    /// ReturnScratch, so clobbering A here is safe.</summary>
    private static void EmitThunk(Emitter e, int bank, Label target)
    {
        LdAAbs(e, CurBank);                                               // LD A, (CurBank)
        e.U8(0xF5);                                                        // PUSH AF   (save caller bank)
        e.U8(0x3E); e.U8(bank);                                            // LD A, bank
        SelectBank(e);                                                     // map callee's bank
        e.Jump(0xCD, target);                                             // CALL callee (windowed)
        e.U8(0xF1);                                                        // POP AF    (caller bank)
        SelectBank(e);                                                     // restore caller's bank
        e.U8(0xC9);                                                        // RET
    }

    /// <summary>Base of the switchable ROM-bank window (0x4000–0x7FFF). This is also the end of the
    /// fixed ROM0 data window: read-only data past here spills into switchable banks.</summary>
    private const int BankWindow = 0x4000;
    private const int BankSize = 0x4000;

    /// <summary>End of the fixed ROM0 data window; data past this spills into switchable banks (the
    /// window ends exactly where the switchable bank window begins).</summary>
    private const int Rom0DataEnd = BankWindow;

    /// <summary>Place a read-only global's bytes into ROM0 data, or — once that 16KB window is full —
    /// into a switchable ROM bank, and return the (windowed) address the global is addressed at. A
    /// banked global's address is only valid while its bank is mapped, so code must select the bank
    /// (write it to 0x2000–0x3FFF, e.g. <c>*(byte*)0x2000 = bank;</c>) before dereferencing it.</summary>
    private static int PlaceRomData(byte[] bytes, List<byte> rom0, List<List<byte>> banks)
    {
        if (bytes.Length > BankSize)
            throw new Sm83LimitException(
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

        // One Tarjan strongly-connected-components pass over the call graph: a function is recursive iff
        // it lies on a call cycle — it belongs to an SCC of more than one function, or it calls itself
        // directly (a self-edge, which forms a single-node cycle Tarjan does not flag on component size).
        var index = new Dictionary<IrFunction, int>(ReferenceEqualityComparer.Instance);
        var low = new Dictionary<IrFunction, int>(ReferenceEqualityComparer.Instance);
        var onStack = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
        var component = new Stack<IrFunction>();
        int next = 0;

        foreach (var start in module.Functions)
        {
            if (index.ContainsKey(start))
                continue;

            // Explicit DFS stack of (node, next-callee-index): simulating the recursion iteratively
            // avoids overflowing the native stack on a deep call chain.
            var dfs = new Stack<(IrFunction Fn, int Ci)>();
            dfs.Push((start, 0));
            while (dfs.Count > 0)
            {
                var (fn, ci) = dfs.Pop();
                var list = callees.TryGetValue(fn, out var cs)
                    ? (IReadOnlyList<IrFunction>)cs : Array.Empty<IrFunction>();

                if (ci == 0)
                {
                    index[fn] = low[fn] = next++;
                    component.Push(fn);
                    onStack.Add(fn);
                }
                else
                {
                    // The callee we descended into (list[ci-1]) has finished; fold its low-link back.
                    low[fn] = Math.Min(low[fn], low[list[ci - 1]]);
                }

                bool descended = false;
                for (int i = ci; i < list.Count; i++)
                {
                    var w = list[i];
                    if (!index.ContainsKey(w))
                    {
                        dfs.Push((fn, i + 1)); // resume fn after w's subtree completes...
                        dfs.Push((w, 0));      // ...having visited w first
                        descended = true;
                        break;
                    }
                    if (onStack.Contains(w))
                        low[fn] = Math.Min(low[fn], index[w]);
                }
                if (descended)
                    continue;

                // fn roots an SCC when its low-link never escaped its own index; pop the component.
                if (low[fn] == index[fn])
                {
                    var members = new List<IrFunction>();
                    IrFunction m;
                    do { m = component.Pop(); onStack.Remove(m); members.Add(m); }
                    while (!ReferenceEquals(m, fn));

                    bool cyclic = members.Count > 1
                        || (callees.TryGetValue(fn, out var self) && self.Any(c => ReferenceEquals(c, fn)));
                    if (cyclic)
                        foreach (var f in members) recursive.Add(f);
                }
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

    // Scratch for the generic width-N memory routines. Operand areas are 16 bytes so the same code
    // serves i32 (N=4), i64 (N=8), and i128 (N=16); N and the loop counter live in single bytes.
    private const int RtN = 0xDF08;         // operand width in bytes (4, 8, or 16)
    private const int RtBits = 0xDF09;      // shift/division loop counter
    private const int RtOpA = 0xDF10;       // multiplicand / dividend -> quotient / shift subject (16 bytes)
    private const int RtOpB = 0xDF20;       // multiplier / divisor (16 bytes)
    private const int RtAcc = 0xDF30;       // product / remainder (16 bytes)

    /// <summary>Where a return value too wide for the register file (i64, i128) is passed: a fixed
    /// 16-byte scratch (little-endian). Public so tests can read it. A recursive or banked function
    /// returns its value here at every width, so the frame/bank restore cannot clobber it.</summary>
    public const int ReturnScratch = 0xDF40;

    // Recursion support: recursive functions save their static frame to a software stack on entry and
    // restore it before return, receive arguments through a fixed staging area (so the caller's own
    // frame is not disturbed), and return via ReturnScratch.
    private const int SoftSp = 0xDF05;      // software-stack pointer (2 bytes)
    private const int CurBank = 0xDF07;     // currently-mapped ROM bank, for far-call thunks
    private const int ArgScratch = 0xDE80;  // recursive-call argument staging (little-endian, packed)

    // The software stack grows up from just above the static frames toward the fixed scratch. It must
    // not reach the heap (which the C# frontend tops at 0xDE00, growing down) or ArgScratch (0xDE80);
    // rt.pushframe traps if it does, turning an otherwise-silent deep-recursion overflow into a halt.
    private const int SoftStackCeiling = 0xDE00;

    // A recursive program relocates the hardware CALL stack from the 127-byte HRAM window (where an
    // overflow runs off into the I/O registers and crashes) into WRAM, growing down from just below
    // ArgScratch. This gives deep recursion the whole [frames, 0xDE80) arena and keeps any overflow
    // contained in RAM (where the software-stack guard can catch the collision) instead of wild.
    private const int HwStackTop = 0xDE80;

    /// <summary>MBC1 ROM-bank select register (a write here maps that bank into 0x4000-0x7FFF).</summary>
    private const int MbcBankSelect = 0x2000;

    /// <summary>__mul16: HL = DE * BC (low 16 bits) by shift-and-add. Sign-agnostic.</summary>
    private static void EmitMul16(Emitter e)
    {
        e.PlaceRoutine("mul16");
        LdHL(e, 0);                              // ld hl, 0
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
        LdHL(e, 0);                              // ld hl, 0     (remainder)
        e.U8(0x3E); e.U8(0x10);                  // ld a, 16
        StAAbs(e, RtCount);                                  // ld (RtCount), a
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
        LdAAbs(e, RtCount);                                  // ld a, (RtCount)
        e.U8(0x3D);                              // dec a
        StAAbs(e, RtCount);                                  // ld (RtCount), a
        e.Jump(0xC2, loop);                      // jp nz, loop
        e.U8(0xC9);                              // ret
    }

    /// <summary>__sdivmod16: signed DE / BC -> quotient DE, remainder HL, via unsigned + sign fixup.</summary>
    private static void EmitSDivMod16(Emitter e)
    {
        e.PlaceRoutine("sdivmod16");
        e.U8(0x7A);                              // ld a, d
        StAAbs(e, RtSignRem);                                      // ld (RtSignRem), a   (dividend sign)
        e.U8(0xA8);                              // xor b
        StAAbs(e, RtSignQuot);                                     // ld (RtSignQuot), a  (sign(D)^sign(B))

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
        LdAAbs(e, RtSignQuot); e.U8(0xE6); e.U8(0x80);             // ld a,(RtSignQuot); and 0x80
        e.Jump(0xCA, qPos);
        NegateDE(e);
        e.Place(qPos);

        var rPos = new Label();
        LdAAbs(e, RtSignRem); e.U8(0xE6); e.U8(0x80);             // ld a,(RtSignRem); and 0x80
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

    private static void LdHL(Emitter e, int imm16) { e.U8(0x21); e.U16(imm16); }
    private static void LdDE(Emitter e, int imm16) { e.U8(0x11); e.U16(imm16); }
    private static void LdAAbs(Emitter e, int addr) { e.U8(0xFA); e.U16(addr); }
    private static void StAAbs(Emitter e, int addr) { e.U8(0xEA); e.U16(addr); }
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
        e.U8(0x7C); StAAbs(e, SoftSp + 1);               // ld a,h ; ld (SoftSp+1),a   (A = high byte, HL = new top)
        var trap = new Label();
        // Heap/scratch ceiling: the software top must stay below the heap (0xDE00, growing down).
        e.U8(0xFE); e.U8(SoftStackCeiling >> 8);         // cp <ceiling high byte>   (A = high byte)
        e.Jump(0xD2, trap);                              // jp nc, trap   (high byte >= ceiling -> overflow)
        // Hardware-stack collision: the software stack (growing up) and the CALL stack (SP, growing down)
        // share the arena. Trap if the new top has reached SP, rather than let the two corrupt each other.
        e.U8(0x08); e.U16(RtOpA);                        // ld (RtOpA), sp   (stash SP; RtOpA is idle in the prologue)
        LdAAbs(e, RtOpA);                                // ld a,(RtOpA)     = SP low
        e.U8(0x95);                                      // sub l            = SP_low - SoftSp_low
        LdAAbs(e, RtOpA + 1);                            // ld a,(RtOpA+1)   = SP high
        e.U8(0x9C);                                      // sbc h            (borrow => SP < SoftSp: collided)
        var ok = new Label();
        e.Jump(0xD2, ok);                                // jp nc, ok   (SP >= SoftSp -> safe)
        e.Place(trap);
        e.Jump(0xC3, trap);                              // jp trap    (spin forever on overflow)
        e.Place(ok);
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
        LdDE(e, RtOpA);                               // ld de, RtOpA
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
        LdDE(e, RtOpB);                               // ld de, RtOpB
        e.U8(0xCD); AddRoutineCall(e, "rt.submem");   // RtAcc -= RtOpB, carry = borrow
        e.Jump(0xDA, restore);                        // jp c, restore   (remainder < divisor)
        LdAAbs(e, RtOpA); e.U8(0xF6); e.U8(0x01); StAAbs(e, RtOpA); // set quotient bit0
        e.Jump(0xC3, next);                           // jp next
        e.Place(restore);
        LdBFromN(e); LdHL(e, RtAcc);
        LdDE(e, RtOpB);                               // ld de, RtOpB
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

}
