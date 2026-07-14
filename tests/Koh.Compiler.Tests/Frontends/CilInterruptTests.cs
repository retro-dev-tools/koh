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
/// <c>[Interrupt("VBlank")]</c> on the CIL path: the frontend reads
/// <c>Koh.GameBoy.InterruptAttribute</c> straight off Cecil metadata (matched by simple type name, like
/// <see cref="CilIntrinsicIndex"/> matches <c>[KohIntrinsic]</c>) and sets
/// <see cref="IrFunction.InterruptVector"/> so the backend wires the handler to its real SM83 vector
/// with a RETI epilogue — mirrors <c>CSharpEndToEndTests</c>' own interrupt coverage
/// (<c>Interrupt_EmitsVectorAndReti</c>, <c>RecursiveInterruptHandler_IsRejected</c>,
/// <c>UnknownInterruptKind_ReportedAsDiagnostic</c>), but assembly-driven: the fixture below is real C#
/// compiled to a real assembly (Debug and Release), lowered from THAT assembly, linked to a ROM, and run
/// on <see cref="GameBoySystem"/> long enough for the real PPU to actually raise VBlank — not merely
/// "it compiled". Keeps its own compile-to-assembly/emulator harness (mirrors <c>CilEndToEndTests</c>'
/// own rationale for not sharing another test class's internals).
/// </summary>
public class CilInterruptTests
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
        "koh-cil-interrupt-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilInterruptAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_irq_{Guid.NewGuid():N}.dll");
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

    private static bool HasError(string source, OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        Frontend(source, level, diagnostics);
        return diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error);
    }

    /// <summary>True when the BACKEND reports an error diagnostic (e.g. a recursive interrupt handler)
    /// rather than throwing — mirrors <c>CSharpEndToEndTests.BackendHasError</c>.</summary>
    private static bool BackendHasError(string source, OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(source, level, new DiagnosticBag());
        var backendDiagnostics = new DiagnosticBag();
        new Sm83Backend().Compile(module, backendDiagnostics);
        return backendDiagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error);
    }

    private static (EmitModel Model, LinkResult Link) CompileAndLink(
        string source,
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(source, level, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        IrOptimizer.Optimize(module); // CompilerDriver's default path (Mem2RegPass does SSA construction).
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var link = new LinkerType().Link([new LinkerInput("cil", model)]);
        return (model, link);
    }

    // ---- Emulator harness -----------------------------------------------------------------------

    private static GameBoySystem Load(EmitModel model, LinkResult link)
    {
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = 0x100;
        return gb;
    }

    // ---- The fixture: a real [Interrupt("VBlank")] handler bumping a static WRAM counter ------
    //
    // Main enables VBlank (IE bit 0) and IME, then spins forever — the real PPU (not a manual IRQ
    // raise) drives VBlank once per emulated frame as long as the LCD stays on, so RunFrame() is the
    // thing actually proving the handler fires, not just that it compiled.
    private const string VBlankCounterSource = """
        using Koh.GameBoy;

        public class Program
        {
            static byte counter;

            [Interrupt("VBlank")]
            static void OnVBlank()
            {
                counter++;
            }

            public static void Main()
            {
                Hardware.IE = 0x01;
                Hardware.EnableInterrupts();
                while (true) { }
            }
        }
        """;

    private static byte RunAndReadCounter(OptimizationLevel level, int frames)
    {
        var (model, link) = CompileAndLink(VBlankCounterSource, level);
        var counterAddress = (ushort)
            link.Symbols.First(s => s.Name == "Program.counter").AbsoluteAddress;
        var gb = Load(model, link);
        for (int i = 0; i < frames; i++)
            gb.RunFrame();
        return gb.DebugReadByte(counterAddress);
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Interrupt_SetsVectorOnTheLoweredFunction(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(VBlankCounterSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();

        var handler = module.Functions.First(f => f.Name.EndsWith("OnVBlank"));
        await Assert.That(handler.InterruptVector).IsEqualTo(0x40); // VBlank vector.

        var main = module.Functions.First(f => f.IsEntry);
        await Assert.That(main.InterruptVector).IsNull();
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Interrupt_ActuallyFires_OnGameBoySystem(OptimizationLevel level)
    {
        // Three emulated frames, each 70224 T-cycles == one full PPU frame (154 scanlines x 456
        // dots) == the exact period GameBoySystem.RunFrame() advances by — so with the LCD on
        // continuously and IME enabled from the very first instruction, VBlank fires exactly once
        // per RunFrame() call.
        var counter = RunAndReadCounter(level, frames: 3);
        await Assert.That(counter).IsEqualTo((byte)3);
    }

    [Test]
    public async Task Interrupt_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debugCounter = RunAndReadCounter(OptimizationLevel.Debug, frames: 5);
        var releaseCounter = RunAndReadCounter(OptimizationLevel.Release, frames: 5);
        await Assert.That(releaseCounter).IsEqualTo(debugCounter);
        await Assert.That(releaseCounter).IsEqualTo((byte)5);
    }

    // ---- Diagnostics ----------------------------------------------------------------------------

    private const string UnknownKindSource = """
        using Koh.GameBoy;

        public class Program
        {
            [Interrupt("Vblnk")]
            static void OnVBlank() { }

            public static void Main()
            {
                Hardware.EnableInterrupts();
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task UnknownInterruptKind_ReportedAsDiagnostic(OptimizationLevel level)
    {
        await Assert.That(HasError(UnknownKindSource, level)).IsTrue();
        // A recognized kind still lowers clean.
        await Assert.That(HasError(VBlankCounterSource, level)).IsFalse();
    }

    private const string RecursiveHandlerSource = """
        using Koh.GameBoy;

        public class Program
        {
            [Interrupt("VBlank")]
            static void OnVBlank()
            {
                OnVBlank();
            }

            public static void Main()
            {
                Hardware.EnableInterrupts();
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task RecursiveInterruptHandler_IsRejected(OptimizationLevel level)
    {
        // The frontend lowers it fine (recursion itself is legal Koh C#); the backend must reject it
        // because a recursive handler's epilogue would need to be a plain RET (memory-return path),
        // incompatible with RETI.
        await Assert.That(BackendHasError(RecursiveHandlerSource, level)).IsTrue();
    }
}
