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

    /// <summary>Fixed HRAM address the boot stub installs the OAM DMA trigger+wait trampoline at (see
    /// <see cref="EmitOamDmaTrampolineInstall"/>). OAM DMA locks the bus to everything but HRAM for
    /// ~161 M-cycles, so this routine's own CODE — not just its call target — must physically reside in
    /// HRAM at runtime; a ROM-resident routine would have its own instruction fetch corrupted mid-wait.
    /// Reserved unconditionally (like <see cref="HwStackTop"/>/<see cref="SoftSp"/>), whether or not a
    /// given program calls <c>Hardware.RunOamDma</c>.</summary>
    internal const int OamDmaTrampoline = 0xFF80;

    /// <summary>Byte length of the installed trampoline (see <see cref="EmitOamDmaTrampolineInstall"/>'s
    /// remarks for the exact instruction sequence this reserves room for).</summary>
    internal const int OamDmaTrampolineSize = 11;

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
                Array.Empty<SectionData>(),
                Array.Empty<SymbolData>(),
                diagnostics.ToArray()
            );
        }
    }

    private EmitModel CompileCore(IrModule module, DiagnosticBag diagnostics)
    {
        var recursive = FindRecursiveFunctions(module);
        CheckNoInterruptReentrancy(module, recursive);

        // A recursive value returns through ReturnScratch and the frame save/restore path emits a plain
        // RET; an interrupt handler must instead end in RETI after restoring the registers its prologue
        // pushed. The two epilogues are incompatible, so a recursive handler is rejected rather than
        // silently emitted with the wrong return.
        foreach (var fn in module.Functions)
            if (fn.InterruptVector is not null && recursive.Contains(fn))
                throw new Sm83LimitException(
                    $"interrupt handler '{fn.Name}' is recursive; an interrupt handler cannot recurse "
                        + "(its epilogue must be RETI with a balanced stack)."
                );

        // Assign global addresses. Initialized (or ROM-space) globals live in a fixed ROM data
        // section; RAM globals get fixed WRAM/HRAM/SRAM addresses. Function frames are placed
        // after the WRAM globals so nothing overlaps.
        var globalAddresses = new Dictionary<IrGlobal, int>(ReferenceEqualityComparer.Instance);
        var romData = new List<byte>();
        // ROM data past the fixed ROM0 window ([DataBase, 0x4000)) spills into switchable ROM banks
        // (physical banks 1, 2, … each windowed at 0x4000–0x7FFF). bankData[i] is bank (i + 1).
        var bankData = new List<List<byte>>();
        int wramGlobals = WramBase,
            // The OAM DMA HRAM trampoline (see EmitOamDmaTrampolineInstall) always owns
            // [OamDmaTrampoline, OamDmaTrampoline + OamDmaTrampolineSize) — reserved unconditionally,
            // like HwStackTop/SoftSp/ArgScratch are always-fixed WRAM addresses regardless of whether a
            // given program recurses, rather than conditionally shifting this cursor per-module.
            hramGlobals = OamDmaTrampoline + OamDmaTrampolineSize,
            sramGlobals = 0xA000;
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
                // [KohAligned(n)] (Koh.GameBoy) rounds the placement cursor up to the next multiple of n
                // before assigning this global's address — e.g. a page-aligned OAM DMA shadow buffer.
                if (g.Alignment is int align && align > 1)
                    wramGlobals = (wramGlobals + align - 1) / align * align;
                globalAddresses[g] = wramGlobals;
                wramGlobals += SizeOf(g.Type);
            }
        }
        // The whole contiguous span this loop's "plain WRAM" bucket assigned — every module-scope static
        // field/array with no <c>FixedAddress</c>/ROM placement/HRAM/SRAM address space. The entry
        // function's boot-only stub zero-clears exactly this range once (see EmitWramGlobalsClear).
        int wramGlobalsSize = wramGlobals - WramBase;

        // The cartridge boots into the frontend-marked entry (Main), or the first non-handler function
        // if the module has none. An interrupt handler must never be the entry: its body runs on every
        // interrupt and ends in RETI, so booting into it re-runs initializers and returns through a
        // nonexistent interrupt frame.
        var entryFunction =
            module.Functions.FirstOrDefault(f => !f.IsExternal && f.IsEntry)
            ?? module.Functions.FirstOrDefault(f => !f.IsExternal && f.InterruptVector is null);

        // Give every function a disjoint WRAM frame so a caller's live values and a callee's
        // storage never overlap (correct for a non-recursive call graph; frames are not yet
        // reused across functions that can't be live simultaneously).
        var allocations = new Dictionary<IrFunction, FunctionAllocation>(
            ReferenceEqualityComparer.Instance
        );
        int wram = wramGlobals;
        foreach (var fn in module.Functions)
        {
            if (fn.IsExternal)
                continue;
            // Residency is disabled for interrupt handlers (their prologue push/pop and RETI epilogue)
            // and recursive functions (software-stack frame save/restore); the conservative model does
            // not yet reason about those register constraints. A parameter may additionally be received in
            // a register (the register calling convention) for any function except the entry, which has no
            // caller to set its registers up.
            bool allowResidency = fn.InterruptVector is null && !recursive.Contains(fn);
            bool allowParamResidency = allowResidency && !ReferenceEquals(fn, entryFunction);
            var allocation = FunctionAllocation.For(fn, wram, allowResidency, allowParamResidency);
            allocations[fn] = allocation;
            wram = allocation.FrameEnd;
        }

        // The software stack (for recursive frame save/restore) occupies WRAM above all static frames,
        // growing up toward the fixed runtime scratch at 0xDF00.
        int softStackBase = wram;

        var emitter = new Emitter();
        var symbols = new List<SymbolData>();

        // The dead-store peephole is unsound when an interrupt handler could asynchronously read a stored
        // WRAM slot between two mainline stores; disable it whenever the module has any handler.
        bool allowDeadStore = !HasInterruptHandler(module);

        // -1 unless the program calls Hardware.RunOamDma (CilLoweringContext.EnsureOamDmaSourceGlobal
        // only ever creates this global from that call's own lowering) — gates the boot-only HRAM
        // trampoline install below (EmitOamDmaTrampolineInstall no-ops when this is negative).
        int oamDmaSrcAddr = OamDmaSourceAddress(globalAddresses);

        var funcOffsets = new List<(IrFunction Fn, int Offset)>();
        foreach (var fn in module.Functions)
        {
            if (fn.IsExternal)
                continue;
            int startOffset = emitter.Code.Count;
            funcOffsets.Add((fn, startOffset));
            new FunctionEmitter(
                emitter,
                fn,
                allocations,
                globalAddresses,
                recursive,
                ReferenceEquals(fn, entryFunction),
                softStackBase,
                wramGlobalsSize: wramGlobalsSize,
                oamDmaSrcAddr: oamDmaSrcAddr
            ).Compile();
            emitter.PeepholeFrom(startOffset, allowDeadStore);
        }
        int funcsEnd = emitter.Code.Count;

        EmitRuntimeRoutines(emitter, HeapAddress(globalAddresses));

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
                        + "which precludes switching to a data bank)."
                );

            codeSplit = 0;
            foreach (var (_, offset) in funcOffsets)
                if (offset <= rom0Budget)
                    codeSplit = offset;
            if (funcsEnd <= rom0Budget)
                codeSplit = funcsEnd; // all functions fit; only runtime banks

            // A single overflow bank stays mapped (no trampolines); more than one needs far-call thunks,
            // which the multi-bank path builds by re-emitting with bank-aware call routing.
            if (total - codeSplit > BankSize)
                return CompileMultiBank(
                    module,
                    funcOffsets,
                    funcsEnd,
                    allocations,
                    globalAddresses,
                    romData,
                    recursive,
                    entryFunction,
                    softStackBase,
                    emitter.NeededRoutines,
                    wramGlobalsSize
                );
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
            symbols.Add(
                new SymbolData(
                    fn.Name,
                    SymbolKind.Label,
                    SymbolVisibility.Exported,
                    CodeSectionName,
                    // SymbolResolver.ResolveAddresses computes `section.PlacedAddress + Value`, so Value
                    // must be section-relative, not `addr` itself — the "CODE" section is always placed
                    // at CodeBase (see the section below), so subtracting it here recovers `addr` there.
                    addr - CodeBase
                )
            );
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
            new(
                CodeSectionName,
                SectionType.Rom0,
                fixedAddress: CodeBase,
                bank: 0,
                data: rom0Code,
                patches: Array.Empty<PatchEntry>(),
                lineMap: rom0Lines
            ),
        };

        // Overflow code in bank 1 (windowed at 0x4000).
        if (codeSplit < total)
        {
            var bankCode = emitter.Code.GetRange(codeSplit, total - codeSplit).ToArray();
            var bankLines = emitter
                .LineMap.Where(e => e.Offset >= codeSplit)
                .Select(e => e with { Offset = e.Offset - codeSplit })
                .ToList();
            sections.Add(
                new SectionData(
                    "CODEX",
                    SectionType.RomX,
                    fixedAddress: BankWindow,
                    bank: 1,
                    data: bankCode,
                    patches: Array.Empty<PatchEntry>(),
                    lineMap: bankLines
                )
            );
        }

        if (romData.Count > 0)
            sections.Add(
                new SectionData(
                    "RODATA",
                    SectionType.Rom0,
                    fixedAddress: DataBase,
                    bank: 0,
                    data: romData.ToArray(),
                    patches: Array.Empty<PatchEntry>()
                )
            );

        // Switchable ROM data banks (bank i+1, all windowed at 0x4000). Present only when code is not
        // banked (the two are mutually exclusive); code reads them after selecting the bank via 0x2000.
        for (int i = 0; i < bankData.Count; i++)
            sections.Add(
                new SectionData(
                    $"ROMX_{i + 1}",
                    SectionType.RomX,
                    fixedAddress: BankWindow,
                    bank: i + 1,
                    data: bankData[i].ToArray(),
                    patches: Array.Empty<PatchEntry>()
                )
            );

        if (entryFunction is not null)
            sections.Add(
                new SectionData(
                    "HEADER",
                    SectionType.Rom0,
                    fixedAddress: 0x0100,
                    bank: 0,
                    data: BuildHeader(entryAddress, extraBanks),
                    patches: Array.Empty<PatchEntry>()
                )
            );

        // Interrupt vectors: `jp <handler>` at 0x40/0x48/0x50/0x58/0x60.
        foreach (var (vector, address) in interruptHandlers)
            sections.Add(
                new SectionData(
                    $"VEC_{vector:X2}",
                    SectionType.Rom0,
                    fixedAddress: vector,
                    bank: 0,
                    data: [0xC3, (byte)(address & 0xFF), (byte)(address >> 8)],
                    patches: Array.Empty<PatchEntry>()
                )
            );

        foreach (var (g, addr) in globalAddresses)
            symbols.Add(
                new SymbolData(
                    g.Name,
                    SymbolKind.Label,
                    SymbolVisibility.Exported,
                    CodeSectionName,
                    // See the function-symbol loop above: Value must be relative to CodeSectionName's
                    // placed address (CodeBase), regardless of which real memory region `addr` lives in
                    // (WRAM/HRAM/SRAM/ROM data) — the resolver just adds CodeBase back at link time.
                    addr - CodeBase
                )
            );

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
        HashSet<string> neededRoutines,
        int wramGlobalsSize = 0
    )
    {
        if (entryFunction is null)
            throw new NotSupportedException("a banked program needs an entry function.");

        // See CompileCore: the dead-store peephole is unsound with an interrupt handler present.
        bool allowDeadStore = !HasInterruptHandler(module);

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
        bool IsRom0(IrFunction f) =>
            ReferenceEquals(f, entryFunction) || f.InterruptVector is not null;
        var bankedList = order.Where(f => !IsRom0(f)).ToList();
        var banked = new HashSet<IrFunction>(bankedList, ReferenceEqualityComparer.Instance);

        // The first pass measured non-banked sizes; a banked function emits larger (its result is copied
        // to ReturnScratch and calls route through thunks), so packing on those sizes could overflow a
        // near-full bank and spuriously reject a program that fits. Re-measure with the banked set.
        var measure = new Emitter();
        foreach (var r in neededRoutines)
            measure.NeededRoutines.Add(r);
        foreach (var f in bankedList)
        {
            int s0 = measure.Code.Count;
            new FunctionEmitter(
                measure,
                f,
                allocations,
                globalAddresses,
                recursive,
                false,
                softStackBase,
                banked
            ).Compile();
            measure.PeepholeFrom(s0, allowDeadStore); // match real emission, or a fit is spuriously rejected
            size[f] = measure.Code.Count - s0;
        }

        var bankOf = new Dictionary<IrFunction, int>(ReferenceEqualityComparer.Instance);
        int bank = 1,
            bankUsed = 0;
        foreach (var f in bankedList)
        {
            int s = size[f];
            if (s > BankSize)
                throw new Sm83LimitException(
                    $"function '{f.Name}' is {s} bytes — larger than a 16 KB ROM bank."
                );
            if (bankUsed + s > BankSize)
            {
                bank++;
                bankUsed = 0;
            }
            bankOf[f] = bank;
            bankUsed += s;
        }
        int bankCount = bankedList.Count == 0 ? 0 : bank;

        var emitter = new Emitter();
        foreach (var r in neededRoutines)
            emitter.NeededRoutines.Add(r);
        var symbols = new List<SymbolData>();
        var funcAddr = new Dictionary<IrFunction, int>(ReferenceEqualityComparer.Instance);

        // --- ROM0 block: boot stub, entry, handlers, runtime, thunks (contiguous, physically based). ---
        // Boot: seed the current-bank shadow and jump to the entry function.
        emitter.U8(0x3E);
        emitter.U8(0x01); // LD A, 1
        SelectBank(emitter); // seed current-bank shadow + MBC1
        // See EmitWramGlobalsClear: every module-scope static field/array with no explicit initializer
        // defaults to zero in C#, and this boot stub — never reached again once execution passes the JP
        // below — is the one place that can run it exactly once regardless of recursion.
        EmitWramGlobalsClear(emitter, wramGlobalsSize);
        // See FunctionEmitter.Compile's own call: same boot-only, run-exactly-once-regardless-of-
        // recursion install, just living in this path's separate boot stub instead (see that method's
        // remarks for why multi-bank mode can't share the single-bank placement).
        EmitOamDmaTrampolineInstall(emitter, OamDmaSourceAddress(globalAddresses));
        if (recursive.Count > 0)
        {
            // One-time recursion setup lives here (not in the entry's prologue) because the JP below lands
            // on the entry's FunctionLabel, past any pre-label bytes. Doing it here also means a recursive
            // CALL to the entry re-enters at FunctionLabel and never re-runs this.
            emitter.U8(0x31);
            emitter.U16(HwStackTop); // LD SP, HwStackTop
            LdHL(emitter, softStackBase); // LD HL, softStackBase
            emitter.U8(0x7D);
            StAAbs(emitter, SoftSp); // LD A,L ; LD (SoftSp),A
            emitter.U8(0x7C);
            StAAbs(emitter, SoftSp + 1); // LD A,H ; LD (SoftSp+1),A
        }
        emitter.Jump(0xC3, emitter.FunctionLabel(entryFunction)); // JP entry

        void EmitFunc(IrFunction f, int addr)
        {
            int startOffset = emitter.Code.Count;
            funcAddr[f] = addr;
            new FunctionEmitter(
                emitter,
                f,
                allocations,
                globalAddresses,
                recursive,
                ReferenceEquals(f, entryFunction),
                softStackBase,
                banked
            ).Compile();
            emitter.PeepholeFrom(startOffset, allowDeadStore);
        }

        foreach (var f in order.Where(IsRom0))
            EmitFunc(f, CodeBase + emitter.Code.Count);

        EmitRuntimeRoutines(emitter, HeapAddress(globalAddresses));

        foreach (var f in bankedList)
        {
            emitter.Place(emitter.ThunkLabel(f));
            EmitThunk(emitter, bankOf[f], emitter.FunctionLabel(f));
        }

        int rom0End = emitter.Code.Count;
        if (rom0End > DataBase - CodeBase)
            throw new Sm83LimitException(
                $"ROM0 code (entry, handlers, runtime, and {bankedList.Count} far-call thunks) is "
                    + $"{rom0End} bytes — more than the {DataBase - CodeBase}-byte ROM0 code window holds."
            );

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
                        + $"{BankSize}-byte bank window. Split the banked functions across more/smaller units."
                );
        }
        int total = emitter.Code.Count;

        emitter.Resolve(regions);

        foreach (var f in order)
            symbols.Add(
                new SymbolData(
                    f.Name,
                    SymbolKind.Label,
                    SymbolVisibility.Exported,
                    CodeSectionName,
                    // See CompileCore: Value must be relative to CodeSectionName's placed address
                    // (CodeBase) — true here too even for a banked function's `funcAddr[f]` (a
                    // BankWindow-relative address in a different section), since the resolver only ever
                    // adds CodeSectionName's placed address back on top of Value.
                    funcAddr[f] - CodeBase
                )
            );
        foreach (var (g, addr) in globalAddresses)
            symbols.Add(
                new SymbolData(
                    g.Name,
                    SymbolKind.Label,
                    SymbolVisibility.Exported,
                    CodeSectionName,
                    addr - CodeBase
                )
            );

        List<LineMapEntry> LinesIn(int start, int end) =>
            emitter
                .LineMap.Where(e => e.Offset >= start && e.Offset < end)
                .Select(e => e with { Offset = e.Offset - start })
                .ToList();

        var sections = new List<SectionData>
        {
            new(
                CodeSectionName,
                SectionType.Rom0,
                fixedAddress: CodeBase,
                bank: 0,
                data: emitter.Code.GetRange(0, rom0End).ToArray(),
                patches: Array.Empty<PatchEntry>(),
                lineMap: LinesIn(0, rom0End)
            ),
        };
        foreach (var (b, start, end) in bankSpans)
            sections.Add(
                new SectionData(
                    $"CODEX_{b}",
                    SectionType.RomX,
                    fixedAddress: BankWindow,
                    bank: b,
                    data: emitter.Code.GetRange(start, end - start).ToArray(),
                    patches: Array.Empty<PatchEntry>(),
                    lineMap: LinesIn(start, end)
                )
            );

        if (romData.Count > 0)
            sections.Add(
                new SectionData(
                    "RODATA",
                    SectionType.Rom0,
                    fixedAddress: DataBase,
                    bank: 0,
                    data: romData.ToArray(),
                    patches: Array.Empty<PatchEntry>()
                )
            );

        sections.Add(
            new SectionData(
                "HEADER",
                SectionType.Rom0,
                fixedAddress: 0x0100,
                bank: 0,
                data: BuildHeader(CodeBase, bankCount),
                patches: Array.Empty<PatchEntry>()
            )
        );

        foreach (var f in order.Where(x => x.InterruptVector is not null))
            sections.Add(
                new SectionData(
                    $"VEC_{f.InterruptVector:X2}",
                    SectionType.Rom0,
                    fixedAddress: f.InterruptVector!.Value,
                    bank: 0,
                    data: [0xC3, (byte)(funcAddr[f] & 0xFF), (byte)(funcAddr[f] >> 8)],
                    patches: Array.Empty<PatchEntry>()
                )
            );

        return new EmitModel(sections, symbols, Array.Empty<Diagnostic>());
    }

    // A composite routine that requires another routine be present even if it never CALLs it directly
    // through RoutineLabel (signed div/rem reuses the unsigned core after adjusting signs).
    private static readonly (string Routine, string Requires)[] RuntimePrereqs =
    [
        ("sdivmod16", "udivmod16"),
        ("sdivmod_wide", "udivmod_wide"),
        ("sdivmod_wide4", "udivmod_wide4"),
    ];

    // Every runtime routine and its emitter, in placement order. Composite routines precede the leaf
    // rt.* helpers they reference so the common case places everything in one pass.
    private static readonly (string Name, Action<Emitter> Emit)[] RuntimeEmitters =
    [
        ("mul16", EmitMul16),
        ("udivmod16", EmitUDivMod16),
        ("sdivmod16", EmitSDivMod16),
        ("mul_wide", EmitMulWide),
        ("udivmod_wide", EmitUDivWide),
        ("sdivmod_wide", EmitSDivWide),
        ("mul_wide4", EmitMulWide4),
        ("udivmod_wide4", EmitUDivWide4),
        ("sdivmod_wide4", EmitSDivWide4),
        ("shl_wide", e => EmitShiftWide(e, "shl_wide", IrBinaryOp.Shl)),
        ("lshr_wide", e => EmitShiftWide(e, "lshr_wide", IrBinaryOp.LShr)),
        ("ashr_wide", e => EmitShiftWide(e, "ashr_wide", IrBinaryOp.AShr)),
        ("rt.clracc", EmitClrAcc),
        ("rt.rlmem", EmitRlMem),
        ("rt.rrmem", EmitRrMem),
        ("rt.addmem", EmitAddMem),
        ("rt.submem", EmitSubMem),
        ("rt.negmem", EmitNegMem),
        ("rt.pushframe", _ => { }), // emitted via the heap-aware path in EmitRuntimeRoutines
        ("rt.popframe", EmitPopFrame),
    ];

    /// <summary>Emit the runtime helper routines that generated code referenced. Emitting a routine can
    /// add more <c>NeededRoutines</c> (the leaf rt.* helpers it CALLs via <c>RoutineLabel</c>), so this
    /// runs to a fixpoint: a routine pulled in after its table position is picked up on the next sweep.
    /// Ordering therefore can't silently leave a referenced label unplaced for <c>Resolve</c>.</summary>
    /// <summary>The fixed WRAM address of the heap pointer (<c>__heap</c>), or -1 if the program has no
    /// heap. The recursion soft-stack guard traps against this live pointer so a rising stack and a
    /// descending heap can't silently overwrite each other.</summary>
    private static int HeapAddress(IReadOnlyDictionary<IrGlobal, int> globals)
    {
        foreach (var (g, addr) in globals)
            if (g.Name == Frontends.Cil.CilLoweringContext.HeapPointerName)
                return addr;
        return -1;
    }

    /// <summary>The fixed WRAM address of the OAM DMA source-page scratch cell (<c>__oamdma_src</c>,
    /// see <c>CilLoweringContext.EnsureOamDmaSourceGlobal</c>), or -1 if the program never calls
    /// <c>Hardware.RunOamDma</c>. The trampoline the boot stub installs (see
    /// <see cref="EmitOamDmaTrampolineInstall"/>) reads the source page back from this exact address.</summary>
    private static int OamDmaSourceAddress(IReadOnlyDictionary<IrGlobal, int> globals)
    {
        foreach (var (g, addr) in globals)
            if (g.Name == Frontends.Cil.CilLoweringContext.OamDmaSourceGlobalName)
                return addr;
        return -1;
    }

    /// <summary>Copy the fixed OAM-DMA trigger+wait trampoline into HRAM at
    /// <see cref="OamDmaTrampoline"/> — boot-only, unconditionally before any recursive re-entry could
    /// see it, exactly like <see cref="EmitWramGlobalsClear"/> (see both call sites' remarks for why
    /// this must run once at true boot rather than as ordinary IR in the entry function's own body).
    /// Emitted only when <paramref name="srcAddr"/> is non-negative (<see cref="OamDmaSourceAddress"/>
    /// found the scratch global — i.e. the program actually calls <c>Hardware.RunOamDma</c>).
    ///
    /// The installed 11-byte routine (assembled by hand, since this is the one piece of Koh-authored
    /// machine code the compiler itself emits, not code selected from IR):
    /// <code>
    ///   LD A,(srcAddr)     ; FA lo hi   - the page RunOamDma staged in the WRAM scratch cell
    ///   LDH (0xFF46),A     ; E0 46      - triggers the DMA (write side effect)
    ///   LD A,50            ; 3E 32      - wait-loop counter
    /// loop:
    ///   DEC A              ; 3D
    ///   JR NZ,loop         ; 20 FD
    ///   RET                ; C9
    /// </code>
    /// The canonical real-hardware Pan Docs/rgbds-tutorial routine this mirrors uses a count of 40,
    /// tuned to land at exactly the emulated 1 M-cycle start delay + 160 M-cycle transfer
    /// (<c>Koh.Emulator.Core.Dma.OamDma</c>) = 161 M-cycles from the LDH trigger to RET. This uses 50
    /// instead — a deliberate ~40 M-cycle margin over the exact minimum, since RET returning even one
    /// M-cycle before the bus unlocks reads a corrupted ROM opcode ($FF = RST 38h) rather than merely
    /// running slow; overshoot costs a few extra M-cycles once per call and is always safe.</summary>
    private static void EmitOamDmaTrampolineInstall(Emitter e, int srcAddr)
    {
        if (srcAddr < 0)
            return;
        void Byte(int addr, int value)
        {
            e.U8(0x3E); // LD A, d8
            e.U8(value & 0xFF);
            e.U8(0xEA); // LD (a16), A
            e.U16(addr);
        }
        int t = OamDmaTrampoline;
        Byte(t + 0, 0xFA); // LD A,(srcAddr) opcode
        Byte(t + 1, srcAddr & 0xFF);
        Byte(t + 2, (srcAddr >> 8) & 0xFF);
        Byte(t + 3, 0xE0); // LDH (0xFF46),A opcode
        Byte(t + 4, 0x46);
        Byte(t + 5, 0x3E); // LD A,50 opcode
        Byte(t + 6, 50);
        Byte(t + 7, 0x3D); // loop: DEC A
        Byte(t + 8, 0x20); // JR NZ, loop
        Byte(t + 9, 0xFD); // rel -3 (loop is 3 bytes back from the instruction after this operand)
        Byte(t + 10, 0xC9); // RET
    }

    private static void EmitRuntimeRoutines(Emitter emitter, int heapAddr)
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
                    if (name == "rt.pushframe")
                        EmitPushFrame(emitter, heapAddr);
                    else
                        emit(emitter);
                    progress = true; // emitting may have appended new leaf routines to NeededRoutines
                }
        }
    }

    /// <summary>Whether the module defines any interrupt handler. Gates the dead-store peephole (an
    /// interrupt can asynchronously read a WRAM slot between two mainline stores), so it must be answered
    /// the same way in every compile path.</summary>
    private static bool HasInterruptHandler(IrModule module) =>
        module.Functions.Any(f => f.InterruptVector is not null);

    /// <summary>Given the bank number in <c>A</c>, make it current: write it to both the CurBank shadow
    /// and the MBC1 bank-select register (0x2000).</summary>
    private static void SelectBank(Emitter e)
    {
        StAAbs(e, CurBank); // LD (CurBank), A
        StAAbs(e, MbcBankSelect); // LD (0x2000), A  (MBC1 switches on this write)
    }

    /// <summary>Emit a ROM0 far-call thunk: save the current bank, map the callee's bank, CALL it
    /// through the 0x4000 window, then restore the caller's bank and return. The callee returns via
    /// ReturnScratch, so clobbering A here is safe.</summary>
    private static void EmitThunk(Emitter e, int bank, Label target)
    {
        LdAAbs(e, CurBank); // LD A, (CurBank)
        e.U8(0xF5); // PUSH AF   (save caller bank)
        e.U8(0x3E);
        e.U8(bank); // LD A, bank
        SelectBank(e); // map callee's bank
        e.Jump(0xCD, target); // CALL callee (windowed)
        e.U8(0xF1); // POP AF    (caller bank)
        SelectBank(e); // restore caller's bank
        e.U8(0xC9); // RET
    }

    /// <summary>Base of the switchable ROM-bank window (0x4000–0x7FFF). This is also the end of the
    /// fixed ROM0 data window: read-only data past here spills into switchable banks.</summary>
    private const int BankWindow = 0x4000;
    private const int BankSize = 0x4000;

    /// <summary>Place a read-only global's bytes into ROM0 data, or — once that 16KB window is full —
    /// into a switchable ROM bank, and return the (windowed) address the global is addressed at. A
    /// banked global's address is only valid while its bank is mapped, so code must select the bank
    /// (write it to 0x2000–0x3FFF, e.g. <c>*(byte*)0x2000 = bank;</c>) before dereferencing it.</summary>
    private static int PlaceRomData(byte[] bytes, List<byte> rom0, List<List<byte>> banks)
    {
        if (bytes.Length > BankSize)
            throw new Sm83LimitException(
                $"ROM global of {bytes.Length} bytes exceeds one {BankSize}-byte ROM bank."
            );

        // Data past the fixed ROM0 window (which ends where the switchable bank window begins) banks.
        if (DataBase + rom0.Count + bytes.Length <= BankWindow)
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
            0xCE,
            0xED,
            0x66,
            0x66,
            0xCC,
            0x0D,
            0x00,
            0x0B,
            0x03,
            0x73,
            0x00,
            0x83,
            0x00,
            0x0C,
            0x00,
            0x0D,
            0x00,
            0x08,
            0x11,
            0x1F,
            0x88,
            0x89,
            0x00,
            0x0E,
            0xDC,
            0xCC,
            0x6E,
            0xE6,
            0xDD,
            0xDD,
            0xD9,
            0x99,
            0xBB,
            0xBB,
            0x67,
            0x63,
            0x6E,
            0x0E,
            0xEC,
            0xCC,
            0xDD,
            0xDC,
            0x99,
            0x9F,
            0xBB,
            0xB9,
            0x33,
            0x3E,
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

        header[0x00] = 0x00; // nop
        header[0x01] = 0xC3; // jp a16
        header[0x02] = (byte)(entryAddress & 0xFF);
        header[0x03] = (byte)(entryAddress >> 8);

        NintendoLogo.CopyTo(header.AsSpan(0x04)); // 0x0104..0x0133

        if (extraBanks > 0)
        {
            header[0x47] = 0x01; // cartridge type: MBC1
            // ROM size byte: 0x00=2 banks(32KB), 0x01=4(64KB), 0x02=8(128KB)… nearest power of two
            // that holds bank 0 plus the extra banks.
            int totalBanks = extraBanks + 1;
            int pow2 = 2;
            byte sizeCode = 0;
            while (pow2 < totalBanks)
            {
                pow2 <<= 1;
                sizeCode++;
            }
            header[0x48] = sizeCode; // ROM size
        }
        // Title bytes (0x0134..) left zero.
        return header;
    }

    /// <summary>The direct (non-external) callees of each function, for cycle detection.</summary>
    private static Dictionary<IrFunction, List<IrFunction>> BuildCalleeGraph(IrModule module)
    {
        var callees = new Dictionary<IrFunction, List<IrFunction>>(
            ReferenceEqualityComparer.Instance
        );
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
                    ? (IReadOnlyList<IrFunction>)cs
                    : Array.Empty<IrFunction>();

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
                        dfs.Push((w, 0)); // ...having visited w first
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
                    do
                    {
                        m = component.Pop();
                        onStack.Remove(m);
                        members.Add(m);
                    } while (!ReferenceEquals(m, fn));

                    bool cyclic =
                        members.Count > 1
                        || (
                            callees.TryGetValue(fn, out var self)
                            && self.Any(c => ReferenceEquals(c, fn))
                        );
                    if (cyclic)
                        foreach (var f in members)
                            recursive.Add(f);
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
    private static void CheckNoInterruptReentrancy(
        IrModule module,
        IReadOnlySet<IrFunction> recursive
    )
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
                        + "Give the handler its own copy of the routine."
                );

        // The wide (i32+) arithmetic and i64/i128/recursive memory-return paths route through fixed runtime
        // scratch (RtOpA/RtOpB/RtAcc/ReturnScratch) that, like a static frame, is not reentrant. If both a
        // handler and main-line touch it, an interrupt mid-computation corrupts the interrupted result.
        bool HandlerWide() =>
            handlers.Any(h => UsesWideScratch(h, recursive))
            || handlerReach.Any(f => UsesWideScratch(f, recursive));
        if (HandlerWide() && mainReach.Any(f => UsesWideScratch(f, recursive)))
            throw new NotSupportedException(
                "an interrupt handler and main-line code both use wide (32/64/128-bit) arithmetic or a "
                    + "memory-returned value; these share fixed runtime scratch that an interrupt firing "
                    + "mid-computation would corrupt. Keep wide arithmetic out of interrupt handlers."
            );
    }

    /// <summary>Whether a function touches the shared, non-reentrant runtime scratch: wide (i32+)
    /// mul/div/rem/shift, or a memory-returned value (i64/i128, or any recursive return).</summary>
    private static bool UsesWideScratch(IrFunction fn, IReadOnlySet<IrFunction> recursive)
    {
        bool MemReturn(IrFunction f) =>
            recursive.Contains(f)
            || (f.ReturnType.Kind != IrTypeKind.Void && SizeOf(f.ReturnType) > 4);
        foreach (var block in fn.Blocks)
        foreach (var instr in block.Instructions)
            switch (instr)
            {
                case BinaryInstruction b
                    when SizeOf(b.Type) > 2
                        && b.Op
                            is IrBinaryOp.Mul
                                or IrBinaryOp.UDiv
                                or IrBinaryOp.SDiv
                                or IrBinaryOp.URem
                                or IrBinaryOp.SRem
                                or IrBinaryOp.Shl
                                or IrBinaryOp.LShr
                                or IrBinaryOp.AShr:
                    return true;
                case RetInstruction { Value: not null } when MemReturn(fn):
                    return true;
                case CallInstruction c when MemReturn(c.Callee):
                    return true;
            }
        return false;
    }

    // Fixed scratch for the (non-reentrant) runtime routines.
    private const int RtCount = 0xDF00; // division bit counter
    private const int RtSignRem = 0xDF01; // signed division: remainder sign
    private const int RtSignQuot = 0xDF02; // signed division: quotient sign
    private const int RtCmpLeft = 0xDF03; // signed compare: sign-flipped top byte of the left operand
    private const int RtCmpRight = 0xDF04; // signed compare: sign-flipped top byte of the right operand

    // Scratch for the generic width-N memory routines. Operand areas are 16 bytes so the same code
    // serves i32 (N=4), i64 (N=8), and i128 (N=16); N and the loop counter live in single bytes.
    private const int RtN = 0xDF08; // operand width in bytes (4, 8, or 16)
    private const int RtBits = 0xDF09; // shift/division loop counter
    private const int RtOpA = 0xDF10; // multiplicand / dividend -> quotient / shift subject (16 bytes)
    private const int RtOpB = 0xDF20; // multiplier / divisor (16 bytes)
    private const int RtAcc = 0xDF30; // product / remainder (16 bytes)

    /// <summary>Where a return value too wide for the register file (i64, i128) is passed: a fixed
    /// 16-byte scratch (little-endian). Public so tests can read it. A recursive or banked function
    /// returns its value here at every width, so the frame/bank restore cannot clobber it.</summary>
    public const int ReturnScratch = 0xDF40;

    // Recursion support: recursive functions save their static frame to a software stack on entry and
    // restore it before return, receive arguments through a fixed staging area (so the caller's own
    // frame is not disturbed), and return via ReturnScratch.
    private const int SoftSp = 0xDF05; // software-stack pointer (2 bytes)
    private const int CurBank = 0xDF07; // currently-mapped ROM bank, for far-call thunks
    private const int ArgScratch = 0xDE80; // recursive-call argument staging (little-endian, packed)

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
        LdHL(e, 0); // ld hl, 0
        var loop = new Label();
        var noadd = new Label();
        e.Place(loop);
        e.U8(0x78); // ld a, b
        e.U8(0xB1); // or c
        e.U8(0xC8); // ret z     (BC == 0 -> done)
        e.U8(0xCB);
        e.U8(0x38); // srl b
        e.U8(0xCB);
        e.U8(0x19); // rr c      (BC >>= 1, carry = old bit0)
        e.Jump(0xD2, noadd); // jp nc, noadd
        e.U8(0x19); // add hl, de
        e.Place(noadd);
        e.U8(0xCB);
        e.U8(0x23); // sla e
        e.U8(0xCB);
        e.U8(0x12); // rl d      (DE <<= 1)
        e.Jump(0xC3, loop); // jp loop
    }

    /// <summary>__udivmod16: DE / BC -> quotient in DE, remainder in HL (unsigned, restoring).</summary>
    private static void EmitUDivMod16(Emitter e)
    {
        e.PlaceRoutine("udivmod16");
        LdHL(e, 0); // ld hl, 0     (remainder)
        e.U8(0x3E);
        e.U8(0x10); // ld a, 16
        StAAbs(e, RtCount); // ld (RtCount), a
        var loop = new Label();
        var dosub = new Label();
        var skip = new Label();
        e.Place(loop);
        e.U8(0xCB);
        e.U8(0x23); // sla e
        e.U8(0xCB);
        e.U8(0x12); // rl d
        e.U8(0xCB);
        e.U8(0x15); // rl l
        e.U8(0xCB);
        e.U8(0x14); // rl h    (shift HL:DE left; quotient bit in E.0)
        e.Jump(0xDA, dosub); // jp c, dosub   (bit16 set -> remainder >= divisor)
        e.U8(0x7D);
        e.U8(0x91); // ld a, l ; sub c
        e.U8(0x7C);
        e.U8(0x98); // ld a, h ; sbc a, b   (carry = HL < BC)
        e.Jump(0xDA, skip); // jp c, skip
        e.Place(dosub);
        e.U8(0x7D);
        e.U8(0x91);
        e.U8(0x6F); // ld a, l ; sub c ; ld l, a
        e.U8(0x7C);
        e.U8(0x98);
        e.U8(0x67); // ld a, h ; sbc a, b ; ld h, a
        e.U8(0xCB);
        e.U8(0xC3); // set 0, e   (quotient bit)
        e.Place(skip);
        LdAAbs(e, RtCount); // ld a, (RtCount)
        e.U8(0x3D); // dec a
        StAAbs(e, RtCount); // ld (RtCount), a
        e.Jump(0xC2, loop); // jp nz, loop
        e.U8(0xC9); // ret
    }

    /// <summary>__sdivmod16: signed DE / BC -> quotient DE, remainder HL, via unsigned + sign fixup.</summary>
    private static void EmitSDivMod16(Emitter e)
    {
        e.PlaceRoutine("sdivmod16");
        e.U8(0x7A); // ld a, d
        StAAbs(e, RtSignRem); // ld (RtSignRem), a   (dividend sign)
        e.U8(0xA8); // xor b
        StAAbs(e, RtSignQuot); // ld (RtSignQuot), a  (sign(D)^sign(B))

        var dePos = new Label();
        e.U8(0x7A);
        e.U8(0xE6);
        e.U8(0x80); // ld a, d ; and 0x80
        e.Jump(0xCA, dePos); // jp z, dePos
        NegateDE(e);
        e.Place(dePos);

        var bcPos = new Label();
        e.U8(0x78);
        e.U8(0xE6);
        e.U8(0x80); // ld a, b ; and 0x80
        e.Jump(0xCA, bcPos); // jp z, bcPos
        NegateBC(e);
        e.Place(bcPos);

        e.Jump(0xCD, e.RoutineLabel("udivmod16")); // call __udivmod16

        var qPos = new Label();
        LdAAbs(e, RtSignQuot);
        e.U8(0xE6);
        e.U8(0x80); // ld a,(RtSignQuot); and 0x80
        e.Jump(0xCA, qPos);
        NegateDE(e);
        e.Place(qPos);

        var rPos = new Label();
        LdAAbs(e, RtSignRem);
        e.U8(0xE6);
        e.U8(0x80); // ld a,(RtSignRem); and 0x80
        e.Jump(0xCA, rPos);
        NegateHL(e);
        e.Place(rPos);

        e.U8(0xC9); // ret
    }

    /// <summary>Two's-complement a 16-bit register pair: <c>xor a; sub lo; ld lo,a; ld a,0; sbc a,hi; ld hi,a</c>.</summary>
    private static void NegatePair(Emitter e, int subLo, int storeLo, int sbcHi, int storeHi)
    {
        e.U8(0xAF);
        e.U8(subLo);
        e.U8(storeLo); // xor a ; sub lo ; ld lo, a
        e.U8(0x3E);
        e.U8(0x00);
        e.U8(sbcHi);
        e.U8(storeHi); // ld a, 0 ; sbc a, hi ; ld hi, a
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

    private static void LdHL(Emitter e, int imm16)
    {
        e.U8(0x21);
        e.U16(imm16);
    }

    private static void LdDE(Emitter e, int imm16)
    {
        e.U8(0x11);
        e.U16(imm16);
    }

    private static void LdAAbs(Emitter e, int addr)
    {
        e.U8(0xFA);
        e.U16(addr);
    }

    private static void StAAbs(Emitter e, int addr)
    {
        e.U8(0xEA);
        e.U16(addr);
    }

    private static void LdBFromN(Emitter e)
    {
        LdAAbs(e, RtN);
        e.U8(0x47);
    } // A=(RtN); LD B,A

    /// <summary>Zero <paramref name="size"/> bytes starting at <see cref="WramBase"/> — the whole
    /// contiguous WRAM-globals region (every module-scope static field/array without an explicit
    /// initializer defaults to zero there). Emitted once, unconditionally, in the entry function's
    /// boot-only stub (see <see cref="FunctionEmitter.Compile"/> and <see cref="CompileMultiBank"/>'s own
    /// boot stub) — never as IR store instructions in the entry function's own callable body, which
    /// re-runs on every recursive re-entry (Main calling Main) and would re-zero the region on every call
    /// instead of only at true boot. Uses a 16-bit BC countdown (unlike the fixed-size, B-only
    /// <c>rt.clracc</c>) since the globals region can exceed 255 bytes.</summary>
    private static void EmitWramGlobalsClear(Emitter e, int size)
    {
        if (size <= 0)
            return;
        LdHL(e, WramBase); // HL = WramBase
        e.U8(0x01); // LD BC, size
        e.U16(size);
        int loopStart = e.Code.Count;
        e.U8(0xAF); // xor a            (A = 0; re-derived each iteration since B/C below clobbers it)
        e.U8(0x22); // ld (hl+), a
        e.U8(0x0B); // dec bc
        e.U8(0x78); // ld a, b
        e.U8(0xB1); // or c             (Z set iff BC == 0)
        int jrOffset = e.Code.Count;
        e.U8(0x20); // jr nz, loop
        e.U8((byte)(loopStart - (jrOffset + 2)));
    }

    /// <summary>HL += (RtN - 1), so a pointer at an operand's low byte moves to its high byte.</summary>
    private static void AdvanceHLToMsb(Emitter e)
    {
        LdAAbs(e, RtN);
        e.U8(0x3D); // ld a,(RtN) ; dec a   -> A = N-1
        e.U8(0x85);
        e.U8(0x6F); // add a,l ; ld l,a
        e.U8(0x3E);
        e.U8(0x00);
        e.U8(0x8C);
        e.U8(0x67); // ld a,0 ; adc a,h ; ld h,a
    }

    /// <summary>rt.clracc: zero the N bytes at RtAcc.</summary>
    private static void EmitClrAcc(Emitter e)
    {
        e.PlaceRoutine("rt.clracc");
        LdBFromN(e); // B = N
        LdHL(e, RtAcc); // HL = RtAcc
        e.U8(0xAF); // xor a  (A = 0)
        var loop = new Label();
        e.Place(loop);
        e.U8(0x22); // ld (hl+), a
        e.U8(0x05); // dec b
        e.Jump(0xC2, loop); // jp nz, loop
        e.U8(0xC9); // ret
    }

    /// <summary>rt.rlmem: rotate the N-byte value at HL left through carry (carry-in preserved). LSB first.</summary>
    private static void EmitRlMem(Emitter e)
    {
        e.PlaceRoutine("rt.rlmem");
        var loop = new Label();
        e.Place(loop);
        e.U8(0xCB);
        e.U8(0x16); // rl (hl)
        e.U8(0x23); // inc hl
        e.U8(0x05); // dec b
        e.Jump(0xC2, loop); // jp nz, loop
        e.U8(0xC9); // ret
    }

    /// <summary>rt.rrmem: rotate the N-byte value at HL right through carry (carry-in preserved). MSB first
    /// (HL points at the high byte and walks down).</summary>
    private static void EmitRrMem(Emitter e)
    {
        e.PlaceRoutine("rt.rrmem");
        var loop = new Label();
        e.Place(loop);
        e.U8(0xCB);
        e.U8(0x1E); // rr (hl)
        e.U8(0x2B); // dec hl
        e.U8(0x05); // dec b
        e.Jump(0xC2, loop); // jp nz, loop
        e.U8(0xC9); // ret
    }

    /// <summary>rt.addmem: (HL..) += (DE..) over B bytes; carry cleared first. Result carry = final carry-out.</summary>
    private static void EmitAddMem(Emitter e)
    {
        e.PlaceRoutine("rt.addmem");
        e.U8(0xAF); // xor a  (clear carry)
        var loop = new Label();
        e.Place(loop);
        e.U8(0x1A); // ld a, (de)   src byte
        e.U8(0x8E); // adc a, (hl)  src + dst + carry
        e.U8(0x77); // ld (hl), a
        e.U8(0x23); // inc hl
        e.U8(0x13); // inc de
        e.U8(0x05); // dec b
        e.Jump(0xC2, loop); // jp nz, loop
        e.U8(0xC9); // ret
    }

    /// <summary>rt.submem: (HL..) -= (DE..) over B bytes; borrow cleared first. Result carry = final borrow.</summary>
    private static void EmitSubMem(Emitter e)
    {
        e.PlaceRoutine("rt.submem");
        e.U8(0xA7); // and a  (clear carry/borrow)
        var loop = new Label();
        e.Place(loop);
        e.U8(0x1A); // ld a, (de)   src byte
        e.U8(0x4F); // ld c, a
        e.U8(0x7E); // ld a, (hl)   dst byte
        e.U8(0x99); // sbc a, c     dst - src - borrow
        e.U8(0x77); // ld (hl), a
        e.U8(0x23); // inc hl
        e.U8(0x13); // inc de
        e.U8(0x05); // dec b
        e.Jump(0xC2, loop); // jp nz, loop
        e.U8(0xC9); // ret
    }

    /// <summary>rt.negmem: two's-complement the N-byte value at HL (over B bytes). LSB first.</summary>
    private static void EmitNegMem(Emitter e)
    {
        e.PlaceRoutine("rt.negmem");
        e.U8(0xA7); // and a  (clear borrow)
        var loop = new Label();
        e.Place(loop);
        e.U8(0x3E);
        e.U8(0x00); // ld a, 0   (flags untouched)
        e.U8(0x9E); // sbc a, (hl)   0 - byte - borrow
        e.U8(0x77); // ld (hl), a
        e.U8(0x23); // inc hl
        e.U8(0x05); // dec b
        e.Jump(0xC2, loop); // jp nz, loop
        e.U8(0xC9); // ret
    }

    /// <summary>rt.pushframe: save B bytes at (DE) to the software stack, advancing SoftSp. Used by a
    /// recursive function's prologue to preserve the caller's copy of the shared static frame.</summary>
    private static void EmitPushFrame(Emitter e, int heapAddr)
    {
        e.PlaceRoutine("rt.pushframe");
        LdAAbs(e, SoftSp);
        e.U8(0x6F); // ld a,(SoftSp)   ; ld l,a
        LdAAbs(e, SoftSp + 1);
        e.U8(0x67); // ld a,(SoftSp+1) ; ld h,a   -> HL = SoftSp
        var loop = new Label();
        e.Place(loop);
        e.U8(0x1A); // ld a,(de)
        e.U8(0x22); // ld (hl+),a
        e.U8(0x13); // inc de
        e.U8(0x05); // dec b
        e.Jump(0xC2, loop); // jp nz, loop
        e.U8(0x7D);
        StAAbs(e, SoftSp); // ld a,l ; ld (SoftSp),a
        e.U8(0x7C);
        StAAbs(e, SoftSp + 1); // ld a,h ; ld (SoftSp+1),a   (A = high byte, HL = new top)
        var trap = new Label();
        if (heapAddr >= 0)
        {
            // Heap ceiling: the software top (HL) must stay below the LIVE heap pointer, which grows down
            // from 0xDE00 as `new`/Mem.Alloc bump it. Comparing against the fixed 0xDE00 start would let
            // the rising stack overwrite already-allocated heap objects before tripping. Trap if HL >= heap.
            LdAAbs(e, heapAddr);
            e.U8(0x95); // ld a,(heap)   ; sub l   = heap_low - soft_low
            LdAAbs(e, heapAddr + 1);
            e.U8(0x9C); // ld a,(heap+1) ; sbc h   (carry => heap < soft top)
            e.Jump(0xDA, trap); // jp c, trap    (heap below the new top -> overflow)
        }
        else
        {
            // No heap in this program: the software top just must not reach the fixed scratch ceiling.
            // A still holds the new top's high byte from the `ld a,h` above.
            e.U8(0xFE);
            e.U8(SoftStackCeiling >> 8); // cp <ceiling high byte>
            e.Jump(0xD2, trap); // jp nc, trap   (high byte >= ceiling -> overflow)
        }
        // Hardware-stack collision: the software stack (growing up) and the CALL stack (SP, growing down)
        // share the arena. Trap if the new top has reached SP, rather than let the two corrupt each other.
        e.U8(0x08);
        e.U16(RtOpA); // ld (RtOpA), sp   (stash SP; RtOpA is idle in the prologue)
        LdAAbs(e, RtOpA); // ld a,(RtOpA)     = SP low
        e.U8(0x95); // sub l            = SP_low - SoftSp_low
        LdAAbs(e, RtOpA + 1); // ld a,(RtOpA+1)   = SP high
        e.U8(0x9C); // sbc h            (borrow => SP < SoftSp: collided)
        var ok = new Label();
        e.Jump(0xD2, ok); // jp nc, ok   (SP >= SoftSp -> safe)
        e.Place(trap);
        e.Jump(0xC3, trap); // jp trap    (spin forever on overflow)
        e.Place(ok);
        e.U8(0xC9); // ret
    }

    /// <summary>rt.popframe: retreat SoftSp by B, then restore B bytes from the software stack to (DE).
    /// Used by a recursive function's epilogue to put the caller's frame back before returning.</summary>
    private static void EmitPopFrame(Emitter e)
    {
        e.PlaceRoutine("rt.popframe");
        LdAAbs(e, SoftSp);
        e.U8(0x90);
        e.U8(0x6F); // ld a,(SoftSp) ; sub b ; ld l,a
        LdAAbs(e, SoftSp + 1);
        e.U8(0xDE);
        e.U8(0x00);
        e.U8(0x67); // ld a,(SoftSp+1) ; sbc a,0 ; ld h,a -> HL = SoftSp-B
        e.U8(0x7D);
        StAAbs(e, SoftSp); // ld a,l ; ld (SoftSp),a
        e.U8(0x7C);
        StAAbs(e, SoftSp + 1); // ld a,h ; ld (SoftSp+1),a
        var loop = new Label();
        e.Place(loop);
        e.U8(0x2A); // ld a,(hl+)
        e.U8(0x12); // ld (de),a
        e.U8(0x13); // inc de
        e.U8(0x05); // dec b
        e.Jump(0xC2, loop); // jp nz, loop
        e.U8(0xC9); // ret
    }

    /// <summary>Load RtBits with N*8 (the full-width bit count).</summary>
    private static void LoadFullBitCount(Emitter e)
    {
        LdAAbs(e, RtN);
        e.U8(0x87);
        e.U8(0x87);
        e.U8(0x87); // add a,a x3  -> A = N*8
        StAAbs(e, RtBits);
    }

    /// <summary>Shift RtOpA left by one bit (N bytes, shifting in 0).</summary>
    private static void ShlOpAOnce(Emitter e)
    {
        LdBFromN(e);
        LdHL(e, RtOpA);
        e.U8(0xAF); // xor a  (clear carry -> shift in 0)
        e.U8(0xCD);
        AddRoutineCall(e, "rt.rlmem");
    }

    /// <summary>Shift RtOpB right by one bit, logical (N bytes, shifting in 0).</summary>
    private static void ShrOpBOnce(Emitter e)
    {
        LdBFromN(e);
        LdHL(e, RtOpB);
        AdvanceHLToMsb(e);
        e.U8(0xAF); // xor a  (clear carry -> logical)
        e.U8(0xCD);
        AddRoutineCall(e, "rt.rrmem");
    }

    /// <summary>Emit the two-byte target of a CALL (0xCD already emitted) to a runtime routine.</summary>
    private static void AddRoutineCall(Emitter e, string routine) =>
        e.CallTarget(e.RoutineLabel(routine));

    /// <summary>Given A = a known-nonzero byte, and RtBits already holding a whole-byte-granularity bit
    /// count whose last (most significant) 8 bits correspond to this byte, refine RtBits down to exact
    /// bit granularity by subtracting this byte's own leading-zero-bit count (0..7), found by rotating A
    /// left through carry until a 1 bit rotates out of bit 7 (bounded: A is nonzero, so some bit is set
    /// and this always terminates within 8 rotations). Only valid for a caller that consumes RtBits
    /// LSB-first overall (so the top significant byte's own leading zero bits are the LAST iterations
    /// that would run, and stopping RtBits early skips exactly them) — see EmitMulWide, which shifts its
    /// tested operand right and is therefore LSB-first end to end. Clobbers A and D.</summary>
    private static void RefineTopByteBitCount(Emitter e)
    {
        e.U8(0x16);
        e.U8(0x00); // ld d,0  (leading-zero-bit counter)
        var bitScan = new Label();
        var bitFound = new Label();
        e.Place(bitScan);
        e.U8(0x07); // rlca   (bit7 -> carry, wraps into bit0)
        e.Jump(0xDA, bitFound); // jp c, bitFound
        e.U8(0x14); // inc d
        e.Jump(0xC3, bitScan); // jp bitScan
        e.Place(bitFound);
        LdAAbs(e, RtBits);
        e.U8(0x92); // sub d
        StAAbs(e, RtBits);
    }

    /// <summary>Scan <paramref name="scratch"/> (an RtN-byte operand) from its high byte down for the
    /// highest nonzero byte, and set RtBits to that operand's exact significant bit count (byte
    /// granularity from the scan, refined to bit granularity via <see cref="RefineTopByteBitCount"/>).
    /// If the operand is entirely zero, emits a `ret` that returns from the ENCLOSING routine directly
    /// (the routine's already-zeroed accumulator is the correct final result in that case) — so this can
    /// only be called as the first thing after establishing that zero result (see rt.clracc in every
    /// caller). Shared by both the generic (any width, via RtN) and the width-4-specialized multiply
    /// routines, since the scan/refine logic itself doesn't depend on how the caller's main loop consumes
    /// RtBits afterward — only <see cref="RefineTopByteBitCount"/>'s LSB-first assumption does, which
    /// every caller of this method satisfies (see its own doc comment).</summary>
    private static void EmitScanAndRefineBitsForMul(Emitter e, int scratch)
    {
        LdHL(e, scratch);
        AdvanceHLToMsb(e); // HL = &scratch[N-1] (highest byte)
        LdBFromN(e); // B = N
        var scan = new Label();
        var found = new Label();
        e.Place(scan);
        e.U8(0x7E); // ld a,(hl)
        e.U8(0xA7); // and a
        e.Jump(0xC2, found); // jp nz, found
        e.U8(0x2B); // dec hl
        e.U8(0x05); // dec b
        e.Jump(0xC2, scan); // jp nz, scan
        e.U8(0xC9); // ret   (B reached 0: operand is entirely zero, product stays 0)
        e.Place(found);
        // B now holds the count of significant bytes (1..N): the scan walked from the top byte down,
        // decrementing B once per zero byte skipped, and stopped at the first nonzero byte with B still
        // counting that byte and everything below it. RtBits = B * 8, then refined to bit granularity
        // using that same top byte's value (HL still points at it; A still holds it from the `ld a,(hl)`
        // that found it, but gets read again for clarity after the intervening B*8 arithmetic).
        e.U8(0x78); // ld a,b
        e.U8(0x87);
        e.U8(0x87);
        e.U8(0x87); // add a,a x3 -> A = B*8
        StAAbs(e, RtBits);
        e.U8(0x7E); // ld a,(hl)  (re-read the top significant byte; HL unchanged since `found`)
        RefineTopByteBitCount(e);
    }

    /// <summary>rt.mul_wide: RtAcc = RtOpA * RtOpB (low N bytes) by shift-and-add.
    ///
    /// Before the bit loop, scans RtOpB from its high byte down for the highest nonzero byte, then
    /// refines that down to the exact bit (see <see cref="EmitScanAndRefineBitsForMul"/> and
    /// <see cref="RefineTopByteBitCount"/>), so the loop runs only over RtOpB's actually-significant
    /// bits. RtOpB's low-order bits are exactly what the loop consumes first (it shifts RtOpB right,
    /// testing bit 0 each time) and its highest set bit is exactly the LAST bit that needs testing, so
    /// this only ever SHORTENS the loop from the end — it changes nothing about which bits get
    /// processed, just stops once every remaining bit is (provably) zero and can only ever contribute
    /// more zero-shifts and no-op adds. CIL's int-promotion routes plenty of genuinely-small
    /// (i16/i8-sourced) values through this i32+ routine (see NarrowPass's class remarks on why the
    /// source of that promotion can't always be undone at the IR level), so this is a real, common-case
    /// win, not just a defensive edge case; a RtOpB that is entirely zero (multiply by zero) skips the
    /// loop outright, since the product is already 0 from rt.clracc above.</summary>
    private static void EmitMulWide(Emitter e)
    {
        e.PlaceRoutine("mul_wide");
        e.U8(0xCD);
        AddRoutineCall(e, "rt.clracc"); // RtAcc = 0
        EmitScanAndRefineBitsForMul(e, RtOpB);

        var loop = new Label();
        var noadd = new Label();
        e.Place(loop);
        LdAAbs(e, RtOpB); // A = multiplier low byte
        e.U8(0x0F); // rrca  -> bit0 into carry
        e.Jump(0xD2, noadd); // jp nc, noadd
        LdBFromN(e); // B = N
        LdHL(e, RtAcc); // HL = RtAcc
        LdDE(e, RtOpA); // ld de, RtOpA
        e.U8(0xCD);
        AddRoutineCall(e, "rt.addmem"); // RtAcc += RtOpA
        e.Place(noadd);
        ShlOpAOnce(e); // RtOpA <<= 1
        ShrOpBOnce(e); // RtOpB >>= 1
        LdAAbs(e, RtBits);
        e.U8(0x3D);
        StAAbs(e, RtBits); // dec RtBits
        e.Jump(0xC2, loop); // jp nz, loop
        e.U8(0xC9); // ret
    }

    /// <summary>rt.mul_wide4: exactly <see cref="EmitMulWide"/>'s algorithm (including the scan/refine
    /// preamble, unchanged and reused as-is), specialized for the one width (N=4, i.e. i32 -- what CIL's
    /// int-promotion routes here for essentially every sub-int32 arithmetic expression) that matters most
    /// for ROM-wide performance. The only difference from the generic routine is the main loop's body:
    /// where EmitMulWide's loop CALLs rt.addmem/rt.rlmem/rt.rrmem (each reloading B from RtN and its own
    /// HL/DE, then driving its own N-counted DEC-B loop) once per bit, this hard-codes N=4 and unrolls
    /// each of those into four straight-line byte operations addressed directly off RtOpA/RtOpB/RtAcc, cutting
    /// the fixed CALL+RET+reload overhead this specific routine pays roughly N-outer-loop-iterations
    /// times over. Only reached when <c>ArithmeticEmitter.EmitWideMulDivRem</c> sees a statically-known
    /// 4-byte operand type; every other width keeps using the generic routine untouched.</summary>
    /// <summary>Inline N=4 zero of the 4 bytes at <paramref name="baseAddr"/>, matching rt.clracc without
    /// the CALL/RET/B-loop overhead.</summary>
    private static void EmitClrAcc4(Emitter e, int baseAddr)
    {
        e.U8(0xAF); // xor a
        StAAbs(e, baseAddr);
        StAAbs(e, baseAddr + 1);
        StAAbs(e, baseAddr + 2);
        StAAbs(e, baseAddr + 3);
    }

    private static void EmitMulWide4(Emitter e)
    {
        e.PlaceRoutine("mul_wide4");

        // Register fast path: unlike division (see EmitUDivWide4's matching check), a product can need
        // the FULL width even when both factors individually fit in 16 bits, so "both operands fit in 16
        // bits" is not by itself enough to safely truncate to a 16-bit mul16 result. But "both operands
        // fit in 8 bits" (<= 255, i.e. bytes 1-3 all zero) IS always safe: the largest possible product,
        // 255*255 = 65025, still fits in 16 bits, so mul16's result needs no more than a zero-extend back
        // into the 4-byte RtAcc slot. This is a plain runtime check on the actual operand values, sound
        // for every input, not a range proof about any particular program.
        LdAAbs(e, RtOpA + 1);
        e.U8(0x47); // ld b,a
        LdAAbs(e, RtOpA + 2);
        e.U8(0xB0); // or b
        e.U8(0x47); // ld b,a
        LdAAbs(e, RtOpA + 3);
        e.U8(0xB0); // or b
        var wide = new Label();
        e.Jump(0xC2, wide); // jp nz, wide
        LdAAbs(e, RtOpB + 1);
        e.U8(0x47); // ld b,a
        LdAAbs(e, RtOpB + 2);
        e.U8(0xB0); // or b
        e.U8(0x47); // ld b,a
        LdAAbs(e, RtOpB + 3);
        e.U8(0xB0); // or b
        e.Jump(0xC2, wide); // jp nz, wide
        e.U8(0x16);
        e.U8(0x00); // ld d,0
        LdAAbs(e, RtOpA);
        e.U8(0x5F); // ld e,a       -> DE = RtOpA (zero-extended)
        e.U8(0x06);
        e.U8(0x00); // ld b,0
        LdAAbs(e, RtOpB);
        e.U8(0x4F); // ld c,a       -> BC = RtOpB (zero-extended)
        e.U8(0xCD);
        AddRoutineCall(e, "mul16"); // product -> HL (exact: <= 255*255, fits 16 bits)
        e.U8(0x7D); // ld a,l
        StAAbs(e, RtAcc);
        e.U8(0x7C); // ld a,h
        StAAbs(e, RtAcc + 1);
        e.U8(0xAF); // xor a
        StAAbs(e, RtAcc + 2);
        StAAbs(e, RtAcc + 3);
        e.U8(0xC9); // ret
        e.Place(wide);

        EmitClrAcc4(e, RtAcc); // RtAcc = 0
        EmitScanAndRefineBitsForMul(e, RtOpB);

        var loop = new Label();
        var noadd = new Label();
        e.Place(loop);
        LdAAbs(e, RtOpB);
        e.U8(0x0F); // rrca -> bit0 into carry
        e.Jump(0xD2, noadd); // jp nc, noadd
        // RtAcc[0..3] += RtOpA[0..3], unrolled (no CALL, no B counter).
        LdHL(e, RtAcc);
        e.U8(0xAF); // xor a  (clear carry)
        for (var k = 0; k < 4; k++)
        {
            LdAAbs(e, RtOpA + k);
            e.U8(0x8E); // adc a,(hl)
            e.U8(0x77); // ld (hl),a
            if (k < 3)
                e.U8(0x23); // inc hl
        }
        e.Place(noadd);
        // RtOpA[0..3] <<= 1, unrolled.
        LdHL(e, RtOpA);
        e.U8(0xAF); // xor a  (clear carry -> shift in 0)
        for (var k = 0; k < 4; k++)
        {
            e.U8(0xCB);
            e.U8(0x16); // rl (hl)
            if (k < 3)
                e.U8(0x23); // inc hl
        }
        // RtOpB[3..0] >>= 1 logical, unrolled (MSB-first walk, matching ShrOpBOnce/rt.rrmem's convention).
        LdHL(e, RtOpB + 3);
        e.U8(0xAF); // xor a  (clear carry -> logical)
        for (var k = 0; k < 4; k++)
        {
            e.U8(0xCB);
            e.U8(0x1E); // rr (hl)
            if (k < 3)
                e.U8(0x2B); // dec hl
        }
        LdAAbs(e, RtBits);
        e.U8(0x3D);
        StAAbs(e, RtBits); // dec RtBits
        e.Jump(0xC2, loop); // jp nz, loop
        e.U8(0xC9); // ret
    }

    /// <summary>Scan the dividend at <paramref name="scratch"/> (an RtN-byte operand) from its high byte
    /// down for the highest nonzero byte, byte-pre-shift it (see the class remarks on
    /// <see cref="EmitUDivWide"/>) so its significant bytes sit at the top of the buffer, then further
    /// bit-pre-shift so its highest set bit lands in bit 7, leaving RtBits set to the operand's exact
    /// significant bit count. If entirely zero, emits a `ret` that returns from the ENCLOSING routine
    /// directly (quotient/remainder are already 0 from rt.clracc/the untouched dividend). Shared by both
    /// the generic (any width, via RtN) and the width-4-specialized divide routines.</summary>
    private static void EmitScanAndShiftForDiv(Emitter e, int scratch)
    {
        LdHL(e, scratch);
        AdvanceHLToMsb(e); // HL = &scratch[N-1] (highest byte)
        LdBFromN(e); // B = N
        var scan = new Label();
        var found = new Label();
        e.Place(scan);
        e.U8(0x7E); // ld a,(hl)
        e.U8(0xA7); // and a
        e.Jump(0xC2, found); // jp nz, found
        e.U8(0x2B); // dec hl
        e.U8(0x05); // dec b
        e.Jump(0xC2, scan); // jp nz, scan
        e.U8(0xC9); // ret   (B reached 0: dividend is entirely zero, quotient/remainder stay 0)
        e.Place(found);
        // B = significant byte count (1..N), same derivation as EmitMulWide's scan. RtBits = B * 8.
        e.U8(0x78); // ld a,b
        e.U8(0x87);
        e.U8(0x87);
        e.U8(0x87); // add a,a x3 -> A = B*8
        StAAbs(e, RtBits);

        // lead = N - B: how many high bytes of the dividend are zero and can be fast-forwarded past.
        LdAAbs(e, RtN);
        e.U8(0x90); // sub b
        var skipShift = new Label();
        e.Jump(0xCA, skipShift); // jp z, skipShift   (lead == 0: dividend already needs every byte)
        e.U8(0x4F); // ld c,a   (stash lead)

        // Pre-shift the dividend left by `lead` bytes: dst walks down from &scratch[N-1], src = dst -
        // lead, for B iterations (copying the B significant bytes up into the top of the buffer), then
        // zero-fill the `lead` bytes now vacated at the bottom.
        LdHL(e, scratch);
        AdvanceHLToMsb(e); // HL = &scratch[N-1]  (dst)
        e.U8(0x5D); // ld e,l
        e.U8(0x54); // ld d,h    -> DE = HL
        e.U8(0x7B); // ld a,e
        e.U8(0x91); // sub c
        e.U8(0x5F); // ld e,a
        e.U8(0x7A); // ld a,d
        e.U8(0xDE);
        e.U8(0x00); // sbc a,0
        e.U8(0x57); // ld d,a    -> DE = HL - lead = &scratch[B-1] (src)

        var copyLoop = new Label();
        e.Place(copyLoop);
        e.U8(0x1A); // ld a,(de)
        e.U8(0x77); // ld (hl),a
        e.U8(0x2B); // dec hl
        e.U8(0x1B); // dec de
        e.U8(0x05); // dec b
        e.Jump(0xC2, copyLoop); // jp nz, copyLoop

        // HL now sits at &scratch[lead-1] (the top of the vacated low region); zero-fill `lead` bytes.
        e.U8(0x41); // ld b,c   -> B = lead
        var zeroLoop = new Label();
        e.Place(zeroLoop);
        e.U8(0xAF); // xor a
        e.U8(0x77); // ld (hl),a
        e.U8(0x2B); // dec hl
        e.U8(0x05); // dec b
        e.Jump(0xC2, zeroLoop); // jp nz, zeroLoop
        e.Place(skipShift);

        // scratch[N-1] now holds the dividend's top significant byte, whichever path got here (already
        // in place when lead was 0, or moved there by the byte copy above). Refine RtBits from byte to
        // bit granularity: unlike EmitMulWide's LSB-first refinement (subtract and stop early), this
        // routine processes MSB-first (it shifts the dividend left, extracting bit 7 each iteration), so
        // the leading zero bits of the top byte are the FIRST iterations that would run, not the last --
        // subtracting them from RtBits alone would skip the wrong end. Instead, physically shift the
        // dividend left by that many bits (0..7, reusing ShlOpAOnce -- the same "shift the full N-byte
        // buffer left by 1" primitive the main loop itself uses) so the true top bit lands in bit 7,
        // THEN reduce RtBits by the same count. This is the bit-granularity continuation of the
        // byte-granularity pre-shift above: both exist to skip iterations that are provably no-ops
        // (shifting a 0 into the remainder, borrowing, clearing the quotient bit) without changing the
        // final quotient/remainder. ShlOpAOnce always shifts RtOpA specifically (not `scratch` in
        // general), which is fine — the only caller today, EmitUDivWide[4], always passes RtOpA.
        LdHL(e, scratch);
        AdvanceHLToMsb(e); // HL = &scratch[N-1]
        e.U8(0x7E); // ld a,(hl)
        e.U8(0x16);
        e.U8(0x00); // ld d,0
        var bitScan = new Label();
        var bitFound = new Label();
        e.Place(bitScan);
        e.U8(0x07); // rlca
        e.Jump(0xDA, bitFound); // jp c, bitFound
        e.U8(0x14); // inc d
        e.Jump(0xC3, bitScan); // jp bitScan
        e.Place(bitFound);
        // D = leading zero bit count (0..7) of the top significant byte.
        LdAAbs(e, RtBits);
        e.U8(0x92); // sub d
        StAAbs(e, RtBits);
        e.U8(0x7A); // ld a,d
        e.U8(0xA7); // and a
        var skipBitShift = new Label();
        e.Jump(0xCA, skipBitShift); // jp z, skipBitShift  (top bit already in bit 7)
        var shiftBitLoop = new Label();
        e.Place(shiftBitLoop);
        ShlOpAOnce(e); // RtOpA <<= 1 (all N bytes)
        e.U8(0x15); // dec d
        e.Jump(0xC2, shiftBitLoop); // jp nz, shiftBitLoop
        e.Place(skipBitShift);
    }

    /// <summary>rt.udivmod_wide: RtOpA / RtOpB -> quotient in RtOpA, remainder in RtAcc (unsigned, restoring).
    ///
    /// Before the bit loop, <see cref="EmitScanAndShiftForDiv"/> fast-forwards past every leading zero
    /// byte and bit of the dividend (RtOpA): each one demonstrably contributes nothing (shifting a 0 bit
    /// out of the dividend into the remainder always keeps the remainder below any nonzero divisor --
    /// borrow, quotient bit 0), so pre-shifting the dividend and running the loop only over its remaining
    /// significant bits lands on the exact same final quotient/remainder as running all N*8 iterations
    /// unshortened. sdivmod_wide already takes the absolute value of both operands before calling here,
    /// so this also transparently speeds up every SDiv/SRem whose operands' true magnitude is small --
    /// exactly the fixed-point math in samples/gb-3d that motivated this fix. A dividend that already
    /// needs every byte and bit takes the unshortened N*8-iteration path unchanged.</summary>
    private static void EmitUDivWide(Emitter e)
    {
        e.PlaceRoutine("udivmod_wide");
        e.U8(0xCD);
        AddRoutineCall(e, "rt.clracc"); // remainder RtAcc = 0
        EmitScanAndShiftForDiv(e, RtOpA);

        var loop = new Label();
        var restore = new Label();
        var next = new Label();
        e.Place(loop);
        // Shift {RtAcc:RtOpA} left by one: RtOpA's high bit flows into RtAcc's low bit.
        LdBFromN(e);
        LdHL(e, RtOpA);
        e.U8(0xAF); // B=N; HL=RtOpA; clear carry
        e.U8(0xCD);
        AddRoutineCall(e, "rt.rlmem"); // RtOpA <<= 1, carry = old MSB
        LdBFromN(e);
        LdHL(e, RtAcc); // B=N; HL=RtAcc (carry preserved)
        e.U8(0xCD);
        AddRoutineCall(e, "rt.rlmem"); // RtAcc <<= 1 with carry-in
        // Try remainder -= divisor.
        LdBFromN(e);
        LdHL(e, RtAcc);
        LdDE(e, RtOpB); // ld de, RtOpB
        e.U8(0xCD);
        AddRoutineCall(e, "rt.submem"); // RtAcc -= RtOpB, carry = borrow
        e.Jump(0xDA, restore); // jp c, restore   (remainder < divisor)
        LdAAbs(e, RtOpA);
        e.U8(0xF6);
        e.U8(0x01);
        StAAbs(e, RtOpA); // set quotient bit0
        e.Jump(0xC3, next); // jp next
        e.Place(restore);
        LdBFromN(e);
        LdHL(e, RtAcc);
        LdDE(e, RtOpB); // ld de, RtOpB
        e.U8(0xCD);
        AddRoutineCall(e, "rt.addmem"); // restore remainder
        e.Place(next);
        LdAAbs(e, RtBits);
        e.U8(0x3D);
        StAAbs(e, RtBits);
        e.Jump(0xC2, loop);
        e.U8(0xC9); // ret
    }

    /// <summary>rt.udivmod_wide4: exactly <see cref="EmitUDivWide"/>'s algorithm (including the shared
    /// <see cref="EmitScanAndShiftForDiv"/> preamble, unchanged and reused as-is), specialized for N=4
    /// the same way <see cref="EmitMulWide4"/> specializes the multiply — the main loop's three CALLs
    /// (rt.rlmem x2, then rt.submem or rt.addmem) each reload B from RtN and re-derive HL/DE before
    /// driving their own N-counted DEC-B loop; this hard-codes N=4 and unrolls each into four
    /// straight-line byte operations instead. The two rt.rlmem calls are order-sensitive — the first
    /// (RtOpA) clears carry itself and its final carry-out (RtOpA[3]'s old MSB) must flow untouched into
    /// the second (RtAcc) as its carry-in, exactly as the generic routine relies on — so nothing between
    /// them here may touch the carry flag either (LD reg,nn and LD (nn),A never do).</summary>
    private static void EmitUDivWide4(Emitter e)
    {
        e.PlaceRoutine("udivmod_wide4");

        // Register fast path: if both operands already fit in 16 bits (bytes 2 and 3 of each are zero
        // -- true for RtOpA/RtOpB here regardless of whether the caller is a genuinely-unsigned UDiv/URem
        // or sdivmod_wide4's already-negated-to-absolute-value SDiv/SRem), the whole scan/shift/N=4-loop
        // machinery below is unnecessary: udivmod16 (Sm83Backend.EmitUDivMod16) computes the exact same
        // mathematical quotient/remainder directly in registers, with no memory scratch and no per-bit
        // loop overhead at all -- roughly an order of magnitude cheaper than even the width-4-specialized
        // wide path above for the same small values. A quotient/remainder of a <=16-bit-by-<=16-bit
        // unsigned division always itself fits in 16 bits (quotient <= dividend, remainder < divisor), so
        // zero-extending udivmod16's 16-bit results back into the 4-byte RtOpA/RtAcc slots is exact, not
        // an approximation. This is a plain runtime check on the actual operand values (not a static
        // range proof), so it is sound for every possible input, not just the fixed-point magnitudes
        // samples/gb-3d happens to use -- unlike a multiply, whose product can need the full 32 bits even
        // when both factors individually fit in 16, a divide's result is always bounded by its dividend.
        LdAAbs(e, RtOpA + 2);
        e.U8(0x47); // ld b,a
        LdAAbs(e, RtOpA + 3);
        e.U8(0xB0); // or b
        var wide = new Label();
        e.Jump(0xC2, wide); // jp nz, wide
        LdAAbs(e, RtOpB + 2);
        e.U8(0x47); // ld b,a
        LdAAbs(e, RtOpB + 3);
        e.U8(0xB0); // or b
        e.Jump(0xC2, wide); // jp nz, wide
        LdAAbs(e, RtOpA);
        e.U8(0x5F); // ld e,a
        LdAAbs(e, RtOpA + 1);
        e.U8(0x57); // ld d,a
        LdAAbs(e, RtOpB);
        e.U8(0x4F); // ld c,a
        LdAAbs(e, RtOpB + 1);
        e.U8(0x47); // ld b,a
        e.U8(0xCD);
        AddRoutineCall(e, "udivmod16"); // quotient -> DE, remainder -> HL
        e.U8(0x7B); // ld a,e
        StAAbs(e, RtOpA);
        e.U8(0x7A); // ld a,d
        StAAbs(e, RtOpA + 1);
        e.U8(0xAF); // xor a
        StAAbs(e, RtOpA + 2);
        StAAbs(e, RtOpA + 3);
        e.U8(0x7D); // ld a,l
        StAAbs(e, RtAcc);
        e.U8(0x7C); // ld a,h
        StAAbs(e, RtAcc + 1);
        e.U8(0xAF); // xor a
        StAAbs(e, RtAcc + 2);
        StAAbs(e, RtAcc + 3);
        e.U8(0xC9); // ret
        e.Place(wide);

        EmitClrAcc4(e, RtAcc); // remainder RtAcc = 0
        EmitScanAndShiftForDiv(e, RtOpA);

        var loop = new Label();
        var restore = new Label();
        var next = new Label();
        e.Place(loop);
        // Shift RtOpA[0..3] left by 1 (carry cleared first): old MSB of RtOpA[3] ends up in carry.
        e.U8(0xAF); // xor a
        LdHL(e, RtOpA);
        for (var k = 0; k < 4; k++)
        {
            e.U8(0xCB);
            e.U8(0x16); // rl (hl)
            if (k < 3)
                e.U8(0x23); // inc hl
        }
        // Shift RtAcc[0..3] left by 1, carry-in preserved from above (LD HL,nn below does not touch it).
        LdHL(e, RtAcc);
        for (var k = 0; k < 4; k++)
        {
            e.U8(0xCB);
            e.U8(0x16); // rl (hl)
            if (k < 3)
                e.U8(0x23); // inc hl
        }
        // RtAcc[0..3] -= RtOpB[0..3], unrolled; carry out = final borrow.
        e.U8(0xA7); // and a  (clear borrow)
        LdHL(e, RtAcc);
        for (var k = 0; k < 4; k++)
        {
            LdAAbs(e, RtOpB + k);
            e.U8(0x4F); // ld c,a
            e.U8(0x7E); // ld a,(hl)
            e.U8(0x99); // sbc a,c
            e.U8(0x77); // ld (hl),a
            if (k < 3)
                e.U8(0x23); // inc hl
        }
        e.Jump(0xDA, restore); // jp c, restore   (remainder < divisor)
        LdAAbs(e, RtOpA);
        e.U8(0xF6);
        e.U8(0x01);
        StAAbs(e, RtOpA); // set quotient bit0
        e.Jump(0xC3, next); // jp next
        e.Place(restore);
        // RtAcc[0..3] += RtOpB[0..3], unrolled (restore the remainder rt.submem just subtracted away).
        e.U8(0xAF); // xor a  (clear carry)
        LdHL(e, RtAcc);
        for (var k = 0; k < 4; k++)
        {
            LdAAbs(e, RtOpB + k);
            e.U8(0x8E); // adc a,(hl)
            e.U8(0x77); // ld (hl),a
            if (k < 3)
                e.U8(0x23); // inc hl
        }
        e.Place(next);
        LdAAbs(e, RtBits);
        e.U8(0x3D);
        StAAbs(e, RtBits);
        e.Jump(0xC2, loop);
        e.U8(0xC9); // ret
    }

    /// <summary>rt.sdivmod_wide: signed RtOpA / RtOpB via the unsigned routine plus sign fixup.</summary>
    private static void EmitSDivWide(Emitter e)
    {
        e.PlaceRoutine("sdivmod_wide");
        // Record signs: remainder takes the dividend's sign; quotient takes sign(dividend) ^ sign(divisor).
        LdHL(e, RtOpA);
        AdvanceHLToMsb(e);
        e.U8(0x7E);
        StAAbs(e, RtSignRem); // A = dividend MSB
        e.U8(0x47); // ld b, a  (save dividend sign byte)
        LdHL(e, RtOpB);
        AdvanceHLToMsb(e);
        e.U8(0x7E); // A = divisor MSB
        e.U8(0xA8); // xor b
        StAAbs(e, RtSignQuot);
        // Negate negative operands.
        var aPos = new Label();
        LdAAbs(e, RtSignRem);
        e.U8(0xE6);
        e.U8(0x80); // and 0x80
        e.Jump(0xCA, aPos);
        LdBFromN(e);
        LdHL(e, RtOpA);
        e.U8(0xCD);
        AddRoutineCall(e, "rt.negmem");
        e.Place(aPos);
        var bPos = new Label();
        LdHL(e, RtOpB);
        AdvanceHLToMsb(e);
        e.U8(0x7E);
        e.U8(0xE6);
        e.U8(0x80); // divisor MSB & 0x80
        e.Jump(0xCA, bPos);
        LdBFromN(e);
        LdHL(e, RtOpB);
        e.U8(0xCD);
        AddRoutineCall(e, "rt.negmem");
        e.Place(bPos);
        e.U8(0xCD);
        AddRoutineCall(e, "udivmod_wide");
        // Fix result signs.
        var qPos = new Label();
        LdAAbs(e, RtSignQuot);
        e.U8(0xE6);
        e.U8(0x80);
        e.Jump(0xCA, qPos);
        LdBFromN(e);
        LdHL(e, RtOpA);
        e.U8(0xCD);
        AddRoutineCall(e, "rt.negmem");
        e.Place(qPos);
        var rPos = new Label();
        LdAAbs(e, RtSignRem);
        e.U8(0xE6);
        e.U8(0x80);
        e.Jump(0xCA, rPos);
        LdBFromN(e);
        LdHL(e, RtAcc);
        e.U8(0xCD);
        AddRoutineCall(e, "rt.negmem");
        e.Place(rPos);
        e.U8(0xC9); // ret
    }

    /// <summary>Inline N=4 two's-complement negate at a compile-time-known base address, matching
    /// rt.negmem's algorithm (LSB first, "and a" to clear borrow, then per byte "ld a,0; sbc a,(hl);
    /// ld (hl),a") exactly, minus the CALL/RET and B-driven loop overhead.</summary>
    private static void EmitNegate4(Emitter e, int baseAddr)
    {
        e.U8(0xA7); // and a  (clear borrow)
        LdHL(e, baseAddr);
        for (var k = 0; k < 4; k++)
        {
            e.U8(0x3E);
            e.U8(0x00); // ld a,0
            e.U8(0x9E); // sbc a,(hl)
            e.U8(0x77); // ld (hl),a
            if (k < 3)
                e.U8(0x23); // inc hl
        }
    }

    /// <summary>rt.sdivmod_wide4: exactly <see cref="EmitSDivWide"/>'s algorithm (sign extraction,
    /// negate-to-absolute, unsigned divide, sign fixup), specialized for N=4 the same way
    /// <see cref="EmitMulWide4"/>/<see cref="EmitUDivWide4"/> specialize their own routines: the
    /// negate-to-absolute and sign-fixup steps use <see cref="EmitNegate4"/> (inline, no CALL/RET/B-loop)
    /// instead of the generic rt.negmem, and the unsigned divide itself calls <c>udivmod_wide4</c>. Every
    /// one of these steps runs at most once per call (not once per bit-loop iteration), but SDiv is
    /// pervasive in the CIL-int-promoted arithmetic this whole family of routines targets (every signed
    /// division in samples/gb-3d's fixed-point math goes through here), so their combined fixed overhead
    /// is still worth trimming.</summary>
    private static void EmitSDivWide4(Emitter e)
    {
        e.PlaceRoutine("sdivmod_wide4");
        LdHL(e, RtOpA);
        AdvanceHLToMsb(e);
        e.U8(0x7E);
        StAAbs(e, RtSignRem); // A = dividend MSB
        e.U8(0x47); // ld b, a  (save dividend sign byte)
        LdHL(e, RtOpB);
        AdvanceHLToMsb(e);
        e.U8(0x7E); // A = divisor MSB
        e.U8(0xA8); // xor b
        StAAbs(e, RtSignQuot);
        var aPos = new Label();
        LdAAbs(e, RtSignRem);
        e.U8(0xE6);
        e.U8(0x80); // and 0x80
        e.Jump(0xCA, aPos);
        EmitNegate4(e, RtOpA);
        e.Place(aPos);
        var bPos = new Label();
        LdHL(e, RtOpB);
        AdvanceHLToMsb(e);
        e.U8(0x7E);
        e.U8(0xE6);
        e.U8(0x80); // divisor MSB & 0x80
        e.Jump(0xCA, bPos);
        EmitNegate4(e, RtOpB);
        e.Place(bPos);
        e.U8(0xCD);
        AddRoutineCall(e, "udivmod_wide4");
        var qPos = new Label();
        LdAAbs(e, RtSignQuot);
        e.U8(0xE6);
        e.U8(0x80);
        e.Jump(0xCA, qPos);
        EmitNegate4(e, RtOpA);
        e.Place(qPos);
        var rPos = new Label();
        LdAAbs(e, RtSignRem);
        e.U8(0xE6);
        e.U8(0x80);
        e.Jump(0xCA, rPos);
        EmitNegate4(e, RtAcc);
        e.Place(rPos);
        e.U8(0xC9); // ret
    }

    /// <summary>rt.shl_wide / rt.lshr_wide / rt.ashr_wide: shift RtOpA by RtBits bits (already clamped).</summary>
    private static void EmitShiftWide(Emitter e, string routine, IrBinaryOp op)
    {
        e.PlaceRoutine(routine);
        var loop = new Label();
        e.Place(loop);
        LdAAbs(e, RtBits);
        e.U8(0xA7); // ld a,(RtBits) ; and a
        e.U8(0xC8); // ret z  (count exhausted)
        e.U8(0x3D);
        StAAbs(e, RtBits); // dec a ; store
        if (op == IrBinaryOp.Shl)
        {
            LdBFromN(e);
            LdHL(e, RtOpA);
            e.U8(0xAF); // B=N; HL=RtOpA; clear carry
            e.U8(0xCD);
            AddRoutineCall(e, "rt.rlmem");
        }
        else
        {
            LdBFromN(e);
            LdHL(e, RtOpA);
            AdvanceHLToMsb(e); // B=N; HL -> RtOpA MSB
            if (op == IrBinaryOp.AShr)
            {
                e.U8(0x7E);
                e.U8(0x07); // ld a,(hl) ; rlca  -> sign bit into carry
            }
            else
            {
                e.U8(0xAF); // xor a  (logical: shift in 0)
            }
            e.U8(0xCD);
            AddRoutineCall(e, "rt.rrmem");
        }
        e.Jump(0xC3, loop); // jp loop
    }

    internal static int SizeOf(IrType type) => type.SizeInBytes;
}
