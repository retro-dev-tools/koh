using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// Graphics library WAVE 1 / slice 1: hardware plumbing for sprites and palettes
/// (<c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c>, build plan slice 1). Proves
/// the new <c>Hardware</c> registers (DMA, OBP0/OBP1, OCPS/OCPD, LYC) round-trip through a REAL
/// compiled assembly -&gt; CilFrontend -&gt; IrVerifier -&gt; Sm83Backend -&gt; Linker -&gt; GameBoySystem
/// pipeline, and that a write to DMA (0xFF46) drives the emulator's real OAM DMA
/// (<c>Koh.Emulator.Core.Dma.OamDma</c>), landing 160 real bytes at 0xFE00. Deliberately keeps its own
/// compile-to-assembly harness rather than depending on another test class's internals, mirroring
/// <see cref="CilEndToEndTests"/>'s own stated rationale for doing the same.
/// </summary>
public class CilSpritePaletteHardwareTests
{
    // ---- Roslyn: compile real C# to a real assembly on disk -----------------------------------

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (
                var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            )
            {
                try
                {
                    builder.Add(MetadataReference.CreateFromFile(path));
                }
                catch (IOException) { }
                catch (BadImageFormatException) { }
            }
        }
        builder.Add(
            MetadataReference.CreateFromFile(typeof(Koh.GameBoy.Hardware).Assembly.Location)
        );
        return builder.ToImmutable();
    });

    private static readonly string ScratchDir = Path.Combine(
        Path.GetTempPath(),
        "koh-cil-sprite-palette-hardware-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilSpritePaletteAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true // headroom for future fixtures here that need Gb.* byte* pointers
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_sprite_pal_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    // ---- Frontend -> IR, verified -------------------------------------------------------------

    private static IrModule Frontend(
        string source,
        OptimizationLevel level,
        DiagnosticBag diagnostics
    )
    {
        var assemblyPath = CompileToAssembly(source, level);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        if (!diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
        {
            var errors = IrVerifier.Verify(module);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "IR verification failed:\n  " + string.Join("\n  ", errors)
                );
        }
        return module;
    }

    private static EmitModel Compile(string source, OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(source, level, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        IrOptimizer.Optimize(module);
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    // ---- Emulator harness (mirrors CilEndToEndTests) -------------------------------------------

    private static GameBoySystem Load(
        EmitModel model,
        out int start,
        out int length,
        HardwareMode mode = HardwareMode.Dmg
    )
    {
        var link = new LinkerType().Link([new LinkerInput("cil", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        length = Sm83Backend.CodeBase + model.Sections[0].Data.Length - 0x100;
        var gb = new GameBoySystem(mode, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    private static void Run(GameBoySystem gb, int start, int length)
    {
        for (int steps = 0; steps < 200_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
    }

    /// <summary>Steps the CPU until it writes the OAM DMA trigger (0xFF46), then stops — real OAM DMA
    /// locks the external bus (ROM included) to $FF for ~161 M-cycles after the trigger (see
    /// <c>Koh.Emulator.Core.Bus.Mmu.ReadByte</c>'s bus-lock comment), so any ROM code fetched during
    /// that window is corrupted. On real hardware (and per this graphics library's own design doc,
    /// §2 "Prerequisite plumbing" item 2) the wait loop MUST run from HRAM; that HRAM trampoline is
    /// the <c>oamdma</c> compiler intrinsic slated for slice 2, not yet built. Slice 1 only adds the
    /// register plumbing, so this harness stops stepping ROM code the instant the trigger fires and
    /// advances the DMA controller directly — still the real
    /// <see cref="Koh.Emulator.Core.Dma.OamDma"/> path, just without a ROM-side wait loop that
    /// doesn't exist yet.</summary>
    private static void RunUntilDmaTriggered(GameBoySystem gb, int start)
    {
        for (int steps = 0; steps < 200_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                throw new InvalidOperationException("program ended before triggering DMA");
            gb.StepInstruction();
            if (gb.DebugReadByte(0xFF46) == 0xC0)
                return;
        }
        throw new InvalidOperationException("DMA was never triggered within the step budget");
    }

    // ---- Fixture 1: plain register round-trip for the new sprite/palette registers -------------
    //
    // LYC/OBP0/OBP1 are plain DMG-visible cells, checked by direct port readback. OCPS/OCPD are NOT
    // plain cells (mirroring BCPS/BCPD, per CgbHalTests): they only exist in CGB mode (DMG reads $FF
    // regardless of what was written — real hardware, checked by IoRegisters.IsCgb), and OCPS' top
    // bit (unset here) would auto-increment the index on every OCPD write if set. Index 0 with
    // auto-increment off keeps the port readback and the underlying palette RAM (gb.Ppu.ObjPalette,
    // verified directly the same way CgbHalTests.SetBackgroundColor_WritesPaletteRam_InCgbMode checks
    // BgPalette) in lockstep.
    private const string RegisterRoundTripSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                Hardware.LYC = 0x5A;
                Hardware.OBP0 = 0xE4;
                Hardware.OBP1 = 0x1B;
                Hardware.OCPS = 0x00;
                Hardware.OCPD = 0x2C;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task RegisterRoundTrip_NewSpritePaletteRegistersReachRealAddresses(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(RegisterRoundTripSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(RegisterRoundTripSource, level),
            out int s,
            out int l,
            HardwareMode.Cgb
        );
        Run(gb, s, l);

        await Assert.That(gb.DebugReadByte(0xFF45)).IsEqualTo((byte)0x5A); // LYC
        await Assert.That(gb.DebugReadByte(0xFF48)).IsEqualTo((byte)0xE4); // OBP0
        await Assert.That(gb.DebugReadByte(0xFF49)).IsEqualTo((byte)0x1B); // OBP1
        await Assert.That(gb.DebugReadByte(0xFF6A)).IsEqualTo((byte)0x00); // OCPS
        await Assert.That(gb.DebugReadByte(0xFF6B)).IsEqualTo((byte)0x2C); // OCPD
        await Assert.That(gb.Ppu.ObjPalette.RawData[0]).IsEqualTo((byte)0x2C); // underlying palette RAM
    }

    // ---- Fixture 2: a write to DMA (0xFF46) drives a real OAM DMA -------------------------------
    //
    // Compiled Main does exactly one thing: trigger DMA from page 0xC0. The 160-byte source pattern
    // at 0xC000 is seeded by the TEST HARNESS via Mmu.WriteByte before running the ROM, not by
    // compiled Koh C# code — deliberately, not a shortcut: Sm83Backend.WramBase is 0xC000, the exact
    // same address Gb.Wram points at, because the backend's static-WRAM allocator places every
    // local/global/runtime-scratch slot for a COMPILED PROGRAM starting there (NESFab-style, see
    // CLAUDE.md). A compiled `byte* wram = Gb.Wram; for (...) wram[i] = ...;` loop would therefore be
    // writing into the same bytes backing its own loop counter/pointer locals, self-clobbering mid-
    // loop (confirmed empirically: it produced a garbled few-byte pattern instead of 1..160). A
    // trivial one-statement Main like this one allocates no locals at all, so seeding 0xC000..0xC09F
    // directly is safe and still exercises the real thing this fixture is about: a compiled ROM
    // writing Hardware.DMA driving the real Koh.Emulator.Core.Dma.OamDma path.
    private const string OamDmaTriggerSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                Hardware.DMA = 0xC0; // source page 0xC000
            }
        }
        """;

    // A real OAM DMA is 1 M-cycle start delay + 160 M-cycles transfer = 161 M-cycles = 644 T-cycles
    // (Koh.Emulator.Core.Dma.OamDma.TickT is called once per T-cycle, 4x per M-cycle — see
    // GameBoySystem.TickOneMCycle; Koh.Emulator.Core.Tests.OamDmaTests uses the same 648/700 margin).
    // Ticking well past that leaves margin without masking a short-count bug, since the assertion
    // loop below checks every byte, not just the first.
    private const int OamDmaTCyclesToComplete = 700;

    private static void SeedOamDmaSource(GameBoySystem gb)
    {
        for (int i = 0; i < 160; i++)
            gb.Mmu.WriteByte((ushort)(0xC000 + i), (byte)(i + 1));
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task OamDmaTrigger_CopiesSourcePageIntoRealOam(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(OamDmaTriggerSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(OamDmaTriggerSource, level), out int s, out int l);
        SeedOamDmaSource(gb);
        RunUntilDmaTriggered(gb, s);
        for (int t = 0; t < OamDmaTCyclesToComplete; t++)
            gb.OamDma.TickT();

        // DMA (0xFF46) itself reads back the triggering page value.
        await Assert.That(gb.DebugReadByte(0xFF46)).IsEqualTo((byte)0xC0);
        await Assert.That(gb.OamDma.IsBusLocking).IsFalse(); // transfer is done

        for (int i = 0; i < 160; i++)
        {
            var actual = gb.DebugReadByte((ushort)(0xFE00 + i));
            await Assert.That(actual).IsEqualTo((byte)(i + 1));
        }
    }

    [Test]
    public async Task OamDmaTrigger_DebugAndReleaseProduceIdenticalObservableState()
    {
        var gbDebug = Load(
            Compile(OamDmaTriggerSource, OptimizationLevel.Debug),
            out int sd,
            out int _
        );
        SeedOamDmaSource(gbDebug);
        RunUntilDmaTriggered(gbDebug, sd);
        for (int t = 0; t < OamDmaTCyclesToComplete; t++)
            gbDebug.OamDma.TickT();

        var gbRelease = Load(
            Compile(OamDmaTriggerSource, OptimizationLevel.Release),
            out int sr,
            out int _
        );
        SeedOamDmaSource(gbRelease);
        RunUntilDmaTriggered(gbRelease, sr);
        for (int t = 0; t < OamDmaTCyclesToComplete; t++)
            gbRelease.OamDma.TickT();

        for (int i = 0; i < 160; i++)
        {
            var debugByte = gbDebug.DebugReadByte((ushort)(0xFE00 + i));
            var releaseByte = gbRelease.DebugReadByte((ushort)(0xFE00 + i));
            await Assert.That(releaseByte).IsEqualTo(debugByte);
        }
    }

    // ---- Fixture 3: the DESKTOP reference build's own Hardware.DMA setter --------------------
    //
    // Not the CIL-frontend/emulator path above — calls Koh.GameBoy.Hardware/Gb directly, in-process,
    // exactly as a `dotnet run` reference build would. Slice 1's design doc explicitly calls out that
    // the desktop host side is critical: "the reference build must render sprites too."
    [Test]
    public async Task DesktopHost_HardwareDmaSetterCopiesWramPageIntoGbOam()
    {
        var actual = new byte[160];
        unsafe
        {
            byte* wram = Koh.GameBoy.Gb.Wram;
            for (int i = 0; i < 160; i++)
                wram[i] = (byte)(i + 1);

            byte* oam = Koh.GameBoy.Gb.Oam;
            for (int i = 0; i < 160; i++)
                oam[i] = 0; // start from a known state; DMA is the only thing that should populate it

            Koh.GameBoy.Hardware.DMA = 0xC0; // page 0xC0 * 0x100 == Gb.Wram's own base address (0xC000)

            for (int i = 0; i < 160; i++)
                actual[i] = oam[i];
        }

        for (int i = 0; i < 160; i++)
            await Assert.That(actual[i]).IsEqualTo((byte)(i + 1));
    }
}
