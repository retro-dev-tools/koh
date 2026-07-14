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
/// Phase-1 CIL frontend, end to end: real C# source compiled by Roslyn to a real assembly on disk
/// (in both Debug and Release IL), lowered by <see cref="CilFrontend"/>, verified, run through the
/// SM83 backend, linked, and executed in <see cref="GameBoySystem"/> — the same harness shape as
/// <see cref="CSharpEndToEndTests"/>, but assembly-driven instead of source-driven (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>).
/// </summary>
public class CilLoweringTests
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
        "koh-cil-frontend-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilTestAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_test_{Guid.NewGuid():N}.dll");
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
        // CLAUDE.md: assert IrVerifier.Verify(module).IsEmpty() for new lowering. Skipped when the
        // frontend itself reported an error (that IR is expected-incomplete).
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

    // ---- Emulator harness (mirrors CSharpEndToEndTests) ---------------------------------------

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

    // ---- Fixtures ------------------------------------------------------------------------------

    private const string RegisterLoopSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                byte v = 0;
                for (int i = 0; i < 5; i++)
                {
                    v = (byte)(v + 1);
                }
                Hardware.BGP = v;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task RegisterLoop_WritesExpectedValue_DebugAndRelease(OptimizationLevel level)
    {
        var gb = Load(Compile(RegisterLoopSource, level), out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)5);
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task RegisterLoop_IrShape_HasEntryFunctionAndFixedAddressGlobal(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(RegisterLoopSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();

        var main = module.FindFunction("Program.Main");
        await Assert.That(main).IsNotNull();
        await Assert.That(main!.IsEntry).IsTrue();

        var bgp = module.Globals.SingleOrDefault(g => g.FixedAddress == 0xFF47);
        await Assert.That(bgp).IsNotNull();
    }

    private const string RegisterReadWriteSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                Hardware.SCY = 7;
                byte v = Hardware.SCY;
                Hardware.BGP = (byte)(v + 1);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task RegisterReadThenWrite_RoundTripsThroughFixedAddressGlobal(
        OptimizationLevel level
    )
    {
        var gb = Load(Compile(RegisterReadWriteSource, level), out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)7);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)8);
    }

    private const string StaticCallSource = """
        using Koh.GameBoy;

        public class Program
        {
            private static byte AddOne(byte x)
            {
                return (byte)(x + 1);
            }

            public static void Main()
            {
                byte v = 0;
                for (int i = 0; i < 3; i++)
                {
                    v = AddOne(v);
                }
                Hardware.BGP = v;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task CallToGameModuleStaticMethod_IsLoweredAsDirectCall(OptimizationLevel level)
    {
        var gb = Load(Compile(StaticCallSource, level), out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)3);
    }

    // A ternary spans a forward branch with a non-empty CIL stack at the join (see class remarks on
    // Deliver/spill allocas in CilMethodLowerer) — Release IL in particular compiles `?:` this way. The
    // condition reads a Hardware register (a genuinely opaque call as far as Roslyn's own optimizer is
    // concerned) rather than a literal, so Release can't constant-fold the whole conditional away; the
    // ternary's result also feeds directly into the assignment with no named local of its own to store
    // into (unlike `byte v = cond ? a : b;`, which Roslyn is free to compile as two conditional stores
    // to `v` — an empty-stack join needing no spill at all).
    private const string TernarySource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                int i = Hardware.SCY;
                Hardware.BGP = (byte)(i < 5 ? 9 : 1);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Ternary_NonEmptyStackJoin_IsSpilledCorrectly(OptimizationLevel level)
    {
        var gb = Load(Compile(TernarySource, level), out int s, out int l);
        gb.DebugWriteByte(0xFF42, 2); // SCY = 2, so the "true" branch (< 5) is taken.
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)9);
    }

    [Test]
    public async Task Ternary_Release_ActuallyExercisesSpillAllocas()
    {
        // Confirms Deliver's spill-alloca path (see CilMethodLowerer class remarks) actually ran for
        // this fixture, rather than the test merely passing because Roslyn materialized the ternary
        // through an ordinary temp local (an empty-stack join needing no spill). Verified against the
        // real emitted IL (dumped via Cecil): Release inlines `Hardware.SCY`'s read straight into the
        // comparison, so Program.Main declares *zero* IL locals — `blt.s`/fallthrough push 1 or 9 and
        // both edges reach the `conv.u1` block with a value still on the CIL stack. The one alloca this
        // asserts on can therefore only be the spill slot Deliver created for that join, not a declared
        // local.
        var diagnostics = new DiagnosticBag();
        var module = Frontend(TernarySource, OptimizationLevel.Release, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        var main = module.FindFunction("Program.Main")!;
        var allocaCount = main.EntryBlock!.Instructions.OfType<AllocaInstruction>().Count();
        await Assert.That(allocaCount).IsEqualTo(1);
    }

    // Arithmetic/bitwise/compare coverage beyond the spine (add/blt/conv.u1): sub, mul, and, or, xor,
    // shl, shr, neg, not, ceq, clt, cgt, plus whatever predicate Roslyn picks for each `if`.
    private const string ArithmeticAndCompareSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                int a = 13;
                int b = 5;
                int r = a - b; // sub -> 8
                r = r * 3; // mul -> 24
                r = r & 0x1F; // and -> 24
                r = r | 0x40; // or -> 88
                r = r ^ 0x08; // xor -> 80
                r = r << 1; // shl -> 160
                r = r >> 2; // shr -> 40
                int neg = -a; // neg -> -13
                int cmpl = ~a; // not -> -14
                byte flags = 0;
                if (r == 40) flags += 1; // ceq
                if (neg < 0) flags += 2; // clt
                if (cmpl > -20) flags += 4; // cgt
                if (a != b) flags += 8;
                if (b <= a) flags += 16;
                if (a >= b) flags += 32;
                Hardware.BGP = flags;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task ArithmeticAndCompareOpcodes_ProduceExpectedValue(OptimizationLevel level)
    {
        var gb = Load(Compile(ArithmeticAndCompareSource, level), out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)63);
    }

    // Two-operand comparison branches in their natural (loop-condition) shape: an unsigned loop hits
    // blt.un/bge.un, an inequality loop hits bne.un/beq — distinct from the signed `blt` the spine's
    // `for` loop already exercises.
    private const string ComparisonLoopsSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                uint u = 0;
                while (u < 5u)
                {
                    u = u + 1u;
                }
                int i = 0;
                while (i != 4)
                {
                    i = i + 1;
                }
                Hardware.BGP = (byte)(u + (uint)i);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task UnsignedAndInequalityLoops_ProduceExpectedValue(OptimizationLevel level)
    {
        var gb = Load(Compile(ComparisonLoopsSource, level), out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)9);
    }

    // conv.i1/i2/u2: explicit narrowing casts to sbyte/short/ushort.
    private const string ConversionsSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                int r = 300;
                sbyte s1 = (sbyte)r; // conv.i1 -> 44
                int k = 70000;
                short s2 = (short)k; // conv.i2 -> 4464
                ushort s3 = (ushort)k; // conv.u2 -> 4464
                int result = s1 + s2 + s3; // 44+4464+4464 = 8972
                Hardware.BGP = (byte)(result & 0xFF); // 8972 & 0xFF = 12
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task NarrowingConversionOpcodes_ProduceExpectedValue(OptimizationLevel level)
    {
        var gb = Load(Compile(ConversionsSource, level), out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)12);
    }

    private const string UnsupportedCallSource = """
        public class Program
        {
            public static void Main()
            {
                System.Console.WriteLine("hi");
            }
        }
        """;

    [Test]
    public async Task UnsupportedExternalCall_ReportsDiagnostic_DoesNotThrow()
    {
        var diagnostics = new DiagnosticBag();
        Frontend(UnsupportedCallSource, OptimizationLevel.Debug, diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error)).IsTrue();
    }
}
