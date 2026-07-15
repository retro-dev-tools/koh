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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// Graphics library WAVE 1 / slice 2: the compiler pair the Sprites module needs
/// (<c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c>, build plan slice 2) —
/// <c>[KohAligned(n)]</c> on a static WRAM global, and the <c>oamdma</c> HRAM-trampoline intrinsic
/// (<c>Hardware.RunOamDma</c>). Deliberately keeps its own compile-to-assembly harness rather than
/// depending on another test class's internals, mirroring <see cref="CilSpritePaletteHardwareTests"/>'s
/// own stated rationale for doing the same.
/// </summary>
public class CilGraphicsSlice2Tests
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
        "koh-cil-graphics-slice2-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilGraphicsSlice2Asm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_gfx_s2_{Guid.NewGuid():N}.dll");
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

    private static EmitModel Compile(string source, OptimizationLevel level, bool optimize = true)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(source, level, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        if (optimize)
            IrOptimizer.Optimize(module);
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    // ---- Emulator harness (mirrors CilSpritePaletteHardwareTests) -------------------------------

    private static GameBoySystem Load(EmitModel model, out int start)
    {
        var link = new LinkerType().Link([new Koh.Linker.Core.LinkerInput("cil", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    /// <summary>Runs to completion, unlike <c>CilSpritePaletteHardwareTests.RunUntilDmaTriggered</c> —
    /// this slice's whole point is that <c>Hardware.RunOamDma</c> lowers to a real CALL into the boot-
    /// installed HRAM trampoline (Sm83Backend.OamDmaTrampoline, at 0xFF80), so PC legitimately visits
    /// HRAM (not just ROM) while the trigger+wait runs. <see cref="GameBoySystem.StepInstruction"/>
    /// ticks <see cref="GameBoySystem.OamDma"/> once per M-cycle
    /// (<c>GameBoySystem.TickOneMCycle</c>), so stepping through the wait loop advances the real DMA
    /// controller with no manual <c>TickT</c> calls needed — the honest end-to-end path the trampoline
    /// exists to enable.</summary>
    private static void Run(GameBoySystem gb, int start)
    {
        for (int steps = 0; steps < 200_000; steps++)
        {
            int pc = gb.Registers.Pc;
            bool inRom = pc >= start && pc < 0x8000;
            bool inHram = pc >= Sm83Backend.OamDmaTrampoline && pc <= 0xFFFF;
            if (!inRom && !inHram)
                break;
            gb.StepInstruction();
        }
    }

    // ---- Fixture A: [KohAligned(n)] rounds a static WRAM global's cursor up ---------------------
    //
    // Pad (1 byte, unaligned) is declared FIRST so its cctor-emitted `newarr;stsfld` idiom is matched
    // by CilStaticFieldSupport.Collect (and so added to module.Globals) before Shadow's own — Collect
    // walks the constructor's instructions in program order, which follows field declaration order —
    // landing Pad at WramBase (0xC000) and advancing the WRAM cursor to 0xC001. [KohAligned(256)] on
    // Shadow must then round that cursor UP to 0xC100, not just happen to land on a multiple of 256 by
    // starting from an already-aligned WramBase (a vacuous pass this ordering rules out).
    private const string AlignedSource = """
        using Koh.GameBoy;

        public class Program
        {
            static byte[] Pad = new byte[1];

            [KohAligned(256)]
            static byte[] Shadow = new byte[160];

            public static void Main() { }
        }
        """;

    [Test]
    public async Task KohAligned_RoundsStaticWramGlobalCursorUpToAlignment()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(AlignedSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var model = Compile(AlignedSource, OptimizationLevel.Debug, optimize: false);
        var link = new LinkerType().Link([new Koh.Linker.Core.LinkerInput("game", model)]);
        await Assert.That(link.Success).IsTrue();

        var pad = link.Symbols.Single(s => s.Name == "Program.Pad");
        await Assert.That(pad.AbsoluteAddress).IsEqualTo((long)Sm83Backend.WramBase);

        var shadow = link.Symbols.Single(s => s.Name == "Program.Shadow");
        await Assert.That(shadow.AbsoluteAddress).IsEqualTo((long)(Sm83Backend.WramBase + 0x100));
        await Assert.That(shadow.AbsoluteAddress % 256).IsEqualTo(0L);
    }

    // ---- Fixture B: Hardware.RunOamDma runs the real trigger+wait from HRAM ----------------------
    //
    // Source page 0xC1 (0xC100), deliberately NOT 0xC0 (0xC000): the "oamdma" intrinsic stages its
    // argument in a dedicated one-byte WRAM scratch global (CilLoweringContext.
    // EnsureOamDmaSourceGlobal) that — Main having no other statics/locals — lands at WramBase
    // (0xC000) itself. Source page 0xC0 would alias the scratch cell's own address, so the compiled
    // store to it would corrupt byte 0 of the very source range DMA reads from; 0xC1 keeps the two
    // apart (mirrors CilSpritePaletteHardwareTests.OamDmaTriggerSource's own zero-locals reasoning,
    // one level further).
    private const string RunOamDmaSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                Hardware.RunOamDma(0xC1);
            }
        }
        """;

    private static void SeedOamDmaSource(GameBoySystem gb)
    {
        for (int i = 0; i < 160; i++)
            gb.Mmu.WriteByte((ushort)(0xC100 + i), (byte)(i + 1));
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task RunOamDma_TriggersRealDmaAndReturnsWithBusUnlocked(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(RunOamDmaSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(RunOamDmaSource, level), out int start);
        SeedOamDmaSource(gb);
        Run(gb, start);

        // The trampoline's own RET only ever executes after the modeled 1 M-cycle start delay + 160
        // M-cycle transfer completes (Sm83Backend.EmitOamDmaTrampolineInstall's margin over the exact
        // minimum) — so by the time Run() falls off the end of the program, the transfer is done.
        await Assert.That(gb.DebugReadByte(0xFF46)).IsEqualTo((byte)0xC1);
        await Assert.That(gb.OamDma.IsBusLocking).IsFalse();

        for (int i = 0; i < 160; i++)
        {
            var actual = gb.DebugReadByte((ushort)(0xFE00 + i));
            await Assert.That(actual).IsEqualTo((byte)(i + 1));
        }
    }

    [Test]
    public async Task RunOamDma_DebugAndReleaseProduceIdenticalObservableOam()
    {
        var gbDebug = Load(Compile(RunOamDmaSource, OptimizationLevel.Debug), out int sd);
        SeedOamDmaSource(gbDebug);
        Run(gbDebug, sd);

        var gbRelease = Load(Compile(RunOamDmaSource, OptimizationLevel.Release), out int sr);
        SeedOamDmaSource(gbRelease);
        Run(gbRelease, sr);

        for (int i = 0; i < 160; i++)
        {
            var debugByte = gbDebug.DebugReadByte((ushort)(0xFE00 + i));
            var releaseByte = gbRelease.DebugReadByte((ushort)(0xFE00 + i));
            await Assert.That(releaseByte).IsEqualTo(debugByte);
        }
    }
}
