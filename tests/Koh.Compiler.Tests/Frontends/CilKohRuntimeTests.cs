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
/// Sanity check for the <c>[KohRuntime]</c> port (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>, §3): <c>Koh.GameBoy.SoftFloat</c>
/// carries the softfloat routines ported from <c>Koh.Compiler.Frontends.CSharp.SoftFloatRuntime</c>,
/// each tagged <c>[KohRuntime("f32.add")]</c> etc. This file does NOT touch the CIL frontend's float
/// lowering (no float-op routing exists yet — that is later work); it proves the routine BODIES are
/// in-subset by calling one as ORDINARY referenced code (the fixture never uses <c>float</c>/
/// <c>double</c> at all — only the raw <c>uint</c> bit patterns the routine itself operates on), the
/// same on-demand referenced-assembly lowering <c>CilReferenceTests</c> already exercises for
/// <c>Mem.Copy</c>/the Hal. A clean <see cref="IrVerifier"/> plus a correct emulator run means
/// <see cref="Koh.GameBoy.SoftFloat.AddF32"/>'s body is fully within the Koh C# subset today.
/// </summary>
public class CilKohRuntimeTests
{
    // ---- Roslyn: compile real C# to a real assembly on disk, referencing Koh.GameBoy -----------

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
        "koh-cil-runtime-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilRuntimeAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_runtime_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    // ---- Frontend -> IR, verified ------------------------------------------------------------

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
        IrOptimizer.Optimize(module); // CompilerDriver's default path (Mem2RegPass does SSA construction).
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    // ---- Emulator harness (mirrors CilReferenceTests) ------------------------------------------

    private static GameBoySystem Load(EmitModel model, out int start, out int length)
    {
        var link = new LinkerType().Link([new LinkerInput("cil", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        length = Sm83Backend.CodeBase + model.Sections[0].Data.Length - 0x100;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
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

    // ============================================================================================
    // SoftFloat.AddF32 called as ordinary referenced code — bit patterns only, no float/double type
    // anywhere in the fixture. 1.5f (0x3FC00000) + 2.5f (0x40200000) = 4.0f (0x40800000).
    // ============================================================================================
    private const string SoftFloatAddSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                uint a = 0x3FC00000; // 1.5f bits
                uint b = 0x40200000; // 2.5f bits
                uint sum = SoftFloat.AddF32(a, b);
                byte ok = (byte)(sum == 0x40800000u ? 1 : 0);
                Hardware.SCX = ok;
                Hardware.SCY = 0xEE; // completion marker
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task SoftFloatAddF32_LowersCleanlyAsOrdinaryReferencedCode(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(SoftFloatAddSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // The routine actually lowered into the module under its real name (proving the call
        // resolved into Koh.GameBoy.SoftFloat, not a diagnostic-swallowed no-op).
        await Assert.That(module.Functions.Select(f => f.Name)).Contains("SoftFloat.AddF32");
    }

    private static (byte Verdict, byte Completed) RunSoftFloatAdd(OptimizationLevel level)
    {
        var gb = Load(Compile(SoftFloatAddSource, level), out int s, out int l);
        Run(gb, s, l);
        return (gb.DebugReadByte(0xFF43), gb.DebugReadByte(0xFF42)); // SCX, SCY
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task SoftFloatAddF32_ComputesTheCorrectIeeeBits(OptimizationLevel level)
    {
        var (verdict, completed) = RunSoftFloatAdd(level);
        await Assert.That(completed).IsEqualTo((byte)0xEE); // Main ran to its natural end
        await Assert.That(verdict).IsEqualTo((byte)1); // 1.5f + 2.5f == 4.0f, bit for bit
    }

    [Test]
    public async Task SoftFloatAddF32_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debug = RunSoftFloatAdd(OptimizationLevel.Debug);
        var release = RunSoftFloatAdd(OptimizationLevel.Release);
        await Assert.That(release).IsEqualTo(debug);
    }
}
