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
/// <c>yield return</c> iterator task (see <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>):
/// lowers Roslyn's generated state-machine class as ordinary code (it lives in the game assembly, so
/// it IS lowerable — unlike LINQ's BCL bodies) and devirtualizes the <c>foreach</c> consumer's
/// <c>GetEnumerator</c>/<c>MoveNext</c>/<c>get_Current</c>/<c>Dispose</c> calls onto the concrete sealed
/// state machine via <see cref="CilMethodLowerer"/>'s concrete-type tracking (<c>Iterators.cs</c>) plus
/// <c>try/finally</c> lowering (every <c>foreach</c> wraps its loop in a <c>finally</c> that disposes the
/// enumerator). Mirrors <see cref="CilLinqTests"/>'s harness shape.
/// </summary>
public class CilIteratorTests
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
        "koh-cil-iterator-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilIterAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_iter_{Guid.NewGuid():N}.dll");
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

    // ---- Emulator harness (mirrors CilLinqTests/CilDelegateTests) -----------------------------

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

    // ---- Fixture: a single counted for-loop with one yield, consumed by foreach -----------------
    //
    // Steps(5) yields 0,1,2,3,4; ConsumeSteps sums them: 0+1+2+3+4 = 10.

    private const string IteratorSource = """
        using Koh.GameBoy;
        using System.Collections.Generic;

        public class Program
        {
            private static IEnumerable<byte> Steps(byte n)
            {
                for (byte i = 0; i < n; i++)
                    yield return i;
            }

            private static int ConsumeSteps(byte n)
            {
                int sum = 0;
                foreach (byte b in Steps(n))
                    sum += b;
                return sum;
            }

            public static void Main()
            {
                Hardware.BGP = (byte)ConsumeSteps(5);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task ForeachOverIterator_VerifiesCleanAndComputesExpectedValue(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(IteratorSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // 0+1+2+3+4 = 10, only if the foreach genuinely drove the real MoveNext/get_Current state
        // machine (not just "didn't crash").
        await Assert.That(RunAndReadBgp(IteratorSource, level)).IsEqualTo((byte)10);
    }

    [Test]
    public async Task ForeachOverIterator_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debug = RunAndReadBgp(IteratorSource, OptimizationLevel.Debug);
        var release = RunAndReadBgp(IteratorSource, OptimizationLevel.Release);
        await Assert.That(release).IsEqualTo(debug);
    }

    // ---- Two independent foreach loops over two separate calls (each its own fresh enumerator) --

    private const string TwoIteratorsSource = """
        using Koh.GameBoy;
        using System.Collections.Generic;

        public class Program
        {
            private static IEnumerable<byte> Steps(byte n)
            {
                for (byte i = 0; i < n; i++)
                    yield return i;
            }

            private static int SumTwice(byte n)
            {
                int total = 0;
                foreach (byte b in Steps(n))
                    total += b;
                foreach (byte b in Steps(n))
                    total += b;
                return total;
            }

            public static void Main()
            {
                Hardware.BGP = (byte)SumTwice(4);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task TwoIndependentForeachLoops_VerifiesCleanAndComputesExpectedValue(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(TwoIteratorsSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // (0+1+2+3) * 2 = 12.
        await Assert.That(RunAndReadBgp(TwoIteratorsSource, level)).IsEqualTo((byte)12);
    }
}
