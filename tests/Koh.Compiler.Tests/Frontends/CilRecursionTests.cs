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
/// Recursion on the CIL frontend, committed as an actual test (previously verified ad hoc — Fib(10) =
/// 55 on the emulator, both configs — but never pinned). Recursion needed NO frontend change: the
/// backend's software-stack/relocated-CALL-stack machinery (see CLAUDE.md's "SM83 backend is an
/// accumulator machine" remarks) is entirely frontend-agnostic, keyed off the IR call graph
/// (<c>IrOptimizer</c>'s cycle detection), not off which frontend produced it. This file exists to
/// prove that on the CIL path specifically, mirroring <c>CSharpEndToEndTests</c>' own
/// <c>Recursion_Factorial</c>/<c>Recursion_Fibonacci</c>/<c>Recursion_Mutual</c>/
/// <c>Recursion_DeepBeyondHardwareStack</c> fixtures but driven off a real compiled ASSEMBLY, in both
/// Debug and Release IL shapes, run on <see cref="GameBoySystem"/> for a real computed value — not
/// merely "it compiled". Keeps its own compile-to-assembly/emulator harness (mirrors
/// <c>CilEndToEndTests</c>'/<c>CilInterruptTests</c>' own rationale for not sharing another test
/// class's internals).
/// </summary>
public class CilRecursionTests
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
        "koh-cil-recursion-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilRecursionAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_rec_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    // ---- Frontend -> IR, verified ---------------------------------------------------------------

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

    // ---- Emulator harness (mirrors CilEndToEndTests/CilInterruptTests) -------------------------

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

    private static void Run(GameBoySystem gb, int start, int length, int stepBudget = 400_000)
    {
        for (int steps = 0; steps < stepBudget; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
    }

    // ============================================================================================
    // Fixture 1: direct recursion — Fibonacci. Tree recursion (two self-calls per frame) stresses
    // the save/restore ordering of the shared static frame on the software stack. Fib(10) = 55, well
    // within a byte, crossed out through SCX (never a raw literal WRAM pointer as scratch — see
    // CilReferenceTests' class remarks on that same rule).
    // ============================================================================================
    private const string FibonacciSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                Hardware.SCX = Fib(10);
            }

            private static byte Fib(byte n)
            {
                if (n < 2)
                    return n;
                return (byte)(Fib((byte)(n - 1)) + Fib((byte)(n - 2)));
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task DirectRecursion_Fibonacci_ComputesExpectedValue(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(FibonacciSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(FibonacciSource, level), out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)55); // Fib(10) = 55
    }

    [Test]
    public async Task DirectRecursion_Fibonacci_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debugGb = Load(
            Compile(FibonacciSource, OptimizationLevel.Debug),
            out int ds,
            out int dl
        );
        Run(debugGb, ds, dl);
        var releaseGb = Load(
            Compile(FibonacciSource, OptimizationLevel.Release),
            out int rs,
            out int rl
        );
        Run(releaseGb, rs, rl);
        await Assert.That(releaseGb.DebugReadByte(0xFF43)).IsEqualTo(debugGb.DebugReadByte(0xFF43));
    }

    // ============================================================================================
    // Fixture 2: mutual recursion — IsEven/IsOdd call each other, so both are in the same call-graph
    // cycle (the backend's save/restore-shared-frame machinery must cover every function in the
    // cycle, not just a single self-recursive one).
    // ============================================================================================
    private const string MutualRecursionSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                Hardware.SCX = IsEven(10) ? (byte)1 : (byte)0;
                Hardware.SCY = IsEven(7) ? (byte)1 : (byte)0;
            }

            private static bool IsEven(byte n)
            {
                if (n == 0)
                    return true;
                return IsOdd((byte)(n - 1));
            }

            private static bool IsOdd(byte n)
            {
                if (n == 0)
                    return false;
                return IsEven((byte)(n - 1));
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task MutualRecursion_IsEvenIsOdd_ComputesExpectedValues(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(MutualRecursionSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(MutualRecursionSource, level), out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)1); // IsEven(10) == true
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)0); // IsEven(7) == false
    }

    [Test]
    public async Task MutualRecursion_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debugGb = Load(
            Compile(MutualRecursionSource, OptimizationLevel.Debug),
            out int ds,
            out int dl
        );
        Run(debugGb, ds, dl);
        var releaseGb = Load(
            Compile(MutualRecursionSource, OptimizationLevel.Release),
            out int rs,
            out int rl
        );
        Run(releaseGb, rs, rl);
        await Assert.That(releaseGb.DebugReadByte(0xFF43)).IsEqualTo(debugGb.DebugReadByte(0xFF43));
        await Assert.That(releaseGb.DebugReadByte(0xFF42)).IsEqualTo(debugGb.DebugReadByte(0xFF42));
    }

    // ============================================================================================
    // Fixture 3: deep recursion — exercises the relocated WRAM call stack. A recursive program moves
    // the hardware CALL stack from the tiny HRAM window into WRAM (SP = HwStackTop, growing down), so
    // recursion runs hundreds of levels deep instead of overflowing the ~60-level HRAM stack into the
    // I/O registers. 500 levels: sum 1..500 = 125250, low byte 66 (mirrors CSharpEndToEndTests'
    // Recursion_DeepBeyondHardwareStack exactly, on the CIL path).
    // ============================================================================================
    private const string DeepRecursionSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                Hardware.SCX = Sum(500);
            }

            private static byte Sum(int n)
            {
                if (n == 0)
                    return 0;
                byte x = (byte)n;
                return (byte)(Sum(n - 1) + x);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task DeepRecursion_ExercisesRelocatedWramCallStack(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DeepRecursionSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(DeepRecursionSource, level), out int s, out int l);
        Run(gb, s, l, stepBudget: 4_000_000);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)66); // sum(1..500) & 0xFF
    }

    [Test]
    public async Task DeepRecursion_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debugGb = Load(
            Compile(DeepRecursionSource, OptimizationLevel.Debug),
            out int ds,
            out int dl
        );
        Run(debugGb, ds, dl, stepBudget: 4_000_000);
        var releaseGb = Load(
            Compile(DeepRecursionSource, OptimizationLevel.Release),
            out int rs,
            out int rl
        );
        Run(releaseGb, rs, rl, stepBudget: 4_000_000);
        await Assert.That(releaseGb.DebugReadByte(0xFF43)).IsEqualTo(debugGb.DebugReadByte(0xFF43));
    }
}
