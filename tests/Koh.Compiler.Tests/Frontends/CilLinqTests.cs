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
/// LINQ pipeline task (see <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>):
/// <c>arr.Where(..).Select(..).Sum()</c> and similar call-site interception (<see
/// cref="CilMethodLowerer"/>'s <c>Linq</c>/<c>Arrays</c> partials) — <c>System.Linq.Enumerable</c>'s
/// own IL bodies are categorically unlowerable BCL code, so the whole chain is rewritten to a loop.
/// Mirrors <see cref="CilDelegateTests"/>'s harness shape.
///
/// The array itself is built with explicit indexed stores (<c>arr[0] = 1; ...</c>), not a collection
/// initializer — a plain <c>int[] arr = { 1, 2, 3, 4, 5 };</c> compiles (in BOTH Debug and Release,
/// confirmed by re-running the design spike's own dumper against this exact shape) to <c>newarr</c> +
/// <c>RuntimeHelpers.InitializeArray</c> against an RVA data blob, which is a separate, unbuilt
/// interception (reading <c>FieldDefinition.InitialValue</c>) — out of this task's scope. Explicit
/// per-element <c>stelem.i4</c> stores need only the opcodes this task already added.
/// </summary>
public class CilLinqTests
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
        "koh-cil-linq-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilLinqAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_linq_{Guid.NewGuid():N}.dll");
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
        IrOptimizer.Optimize(module); // CompilerDriver's default path (Mem2RegPass does SSA construction).
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    // ---- Emulator harness (mirrors CilEndToEndTests/CilDelegateTests) -------------------------

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

    private static byte RunAndReadBgp(string source, OptimizationLevel level)
    {
        var gb = Load(Compile(source, level), out int s, out int l);
        Run(gb, s, l);
        return gb.DebugReadByte(0xFF47);
    }

    // ---- Fixture: arr.Where(x => x > 2).Select(x => x * 2).Sum() over {1,2,3,4,5} --------------
    //
    // Kept elements after Where: {3,4,5}; after Select (*2): {6,8,10}; Sum = 24.

    private const string LinqSumSource = """
        using Koh.GameBoy;
        using System.Linq;

        public class Program
        {
            private static int Pipeline()
            {
                int[] arr = new int[5];
                arr[0] = 1;
                arr[1] = 2;
                arr[2] = 3;
                arr[3] = 4;
                arr[4] = 5;
                return arr.Where(x => x > 2).Select(x => x * 2).Sum();
            }

            public static void Main()
            {
                Hardware.BGP = (byte)Pipeline();
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task WhereSelectSum_VerifiesCleanAndComputesExpectedValue(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(LinqSumSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // {1,2,3,4,5} -> Where(>2) -> {3,4,5} -> Select(*2) -> {6,8,10} -> Sum = 24.
        await Assert.That(RunAndReadBgp(LinqSumSource, level)).IsEqualTo((byte)24);
    }

    [Test]
    public async Task WhereSelectSum_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debug = RunAndReadBgp(LinqSumSource, OptimizationLevel.Debug);
        var release = RunAndReadBgp(LinqSumSource, OptimizationLevel.Release);
        await Assert.That(release).IsEqualTo(debug);
    }

    // ---- Fixture: Max() directly on an array (no pipeline) --------------------------------------

    private const string MaxSource = """
        using Koh.GameBoy;
        using System.Linq;

        public class Program
        {
            private static int MaxOfArray()
            {
                int[] arr = new int[4];
                arr[0] = 3;
                arr[1] = 9;
                arr[2] = 1;
                arr[3] = 7;
                return arr.Max();
            }

            public static void Main()
            {
                Hardware.BGP = (byte)MaxOfArray();
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task MaxDirectlyOnArray_VerifiesCleanAndComputesExpectedValue(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(MaxSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
        await Assert.That(RunAndReadBgp(MaxSource, level)).IsEqualTo((byte)9);
    }

    [Test]
    public async Task MaxDirectlyOnArray_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debug = RunAndReadBgp(MaxSource, OptimizationLevel.Debug);
        var release = RunAndReadBgp(MaxSource, OptimizationLevel.Release);
        await Assert.That(release).IsEqualTo(debug);
    }

    // ---- Escape hatch: Max() after a pipeline is out of the supported surface -------------------

    private const string MaxAfterPipelineSource = """
        using Koh.GameBoy;
        using System.Linq;

        public class Program
        {
            private static int Bad()
            {
                int[] arr = new int[3];
                arr[0] = 1;
                arr[1] = 2;
                arr[2] = 3;
                return arr.Where(x => x > 0).Max();
            }

            public static void Main()
            {
                Hardware.BGP = (byte)Bad();
            }
        }
        """;

    [Test]
    public async Task MaxAfterPipeline_IsDiagnosedNotThrown()
    {
        var diagnostics = new DiagnosticBag();
        var assemblyPath = CompileToAssembly(MaxAfterPipelineSource, OptimizationLevel.Release);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error)).IsTrue();
        await Assert.That(diagnostics.Any(d => d.Message.Contains("Max"))).IsTrue();
    }
}
