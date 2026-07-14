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
/// Task 2 (generics + wide integers) for the CIL frontend (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>'s "Generic instantiation from IL"
/// hard part): a generic method instantiated at two different types, transitively, end to end; and a
/// long/ulong arithmetic round trip (add/sub/mul/div/rem, including unsigned division/remainder, which
/// phase 1 never wired up at all — see <see cref="CilMethodLowerer.LowerCall"/>'s new
/// <c>Code.Div</c>/<c>Code.Div_Un</c>/<c>Code.Rem</c>/<c>Code.Rem_Un</c> cases plus
/// <c>Code.Ldc_I8</c>/<c>Code.Conv_I8</c>/<c>Code.Conv_U8</c>). Self-contained harness, deliberately not
/// shared with <see cref="CilLoweringTests"/>/<see cref="CilEndToEndTests"/> — matches those files' own
/// stated rationale (each end-to-end contract stays independently readable).
/// </summary>
public class CilGenericTests
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
        "koh-cil-generic-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilGenericAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_generic_{Guid.NewGuid():N}.dll");
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

    // ---- Emulator harness (mirrors CilLoweringTests/CilEndToEndTests) -------------------------

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

    private static byte RunBgp(string source, OptimizationLevel level)
    {
        var gb = Load(Compile(source, level), out int s, out int l);
        Run(gb, s, l);
        return gb.DebugReadByte(0xFF47);
    }

    /// <summary>Read a 64-bit return value out of <see cref="Sm83Backend.ReturnScratch"/> — i64 returns
    /// via memory, not a register (CLAUDE.md: "i64 ... in memory (ReturnScratch)"). Reads all 8 bytes
    /// (not just the low byte(s)) so a truncation bug in the wide-int path can't pass by accident.</summary>
    private static ulong RunI64(string source, OptimizationLevel level)
    {
        var gb = Load(Compile(source, level), out int s, out int l);
        Run(gb, s, l);
        ulong result = 0;
        for (int i = 0; i < 8; i++)
            result |= (ulong)gb.DebugReadByte((ushort)(Sm83Backend.ReturnScratch + i)) << (8 * i);
        return result;
    }

    // ---- Fixture 1: a generic method instantiated at two different types, transitively ---------
    //
    // Wrap<T> is itself generic and calls Identity<T> — a nested generic call inside a generic
    // template's own shared IL body, so instantiating Wrap<byte>/Wrap<ushort> must transitively
    // instantiate Identity<byte>/Identity<ushort> too (see CilMethodLowerer.Generics.cs's
    // LowerGenericCall remarks on transitivity). Real generic arithmetic on T isn't legal standard C#
    // without generic-math constraints, so this fixture proves monomorphization itself (distinct
    // per-instantiation storage/identity) via passthrough + a final combine of the two concrete results
    // — a caching bug that aliased both instantiations onto one IrFunction would silently truncate the
    // ushort path through the byte-sized one and produce a wrong, checkable value (see the arithmetic
    // below).
    private const string GenericTransitiveSource = """
        using Koh.GameBoy;

        public class Program
        {
            static T Identity<T>(T x) => x;
            static T Wrap<T>(T x) => Identity<T>(x);

            public static void Main()
            {
                byte b = Wrap<byte>(5);
                ushort u = Wrap<ushort>(1000);
                Hardware.BGP = (byte)(b + (u >> 3));
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task GenericMethod_TwoTypesTransitively_ProducesExpectedValue(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(GenericTransitiveSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // Four DISTINCT specializations must exist — one Identity/Wrap pair per instantiated type. If
        // the generic-instance cache incorrectly keyed by MethodDefinition alone (ignoring the mangled
        // suffix), the second instantiation would silently reuse the first's IrFunction and this count
        // would be 2, not 4.
        await Assert
            .That(module.Functions.Any(f => f.Name == "Program.Identity__g1_4_byte"))
            .IsTrue();
        await Assert
            .That(module.Functions.Any(f => f.Name == "Program.Identity__g1_6_ushort"))
            .IsTrue();
        await Assert.That(module.Functions.Any(f => f.Name == "Program.Wrap__g1_4_byte")).IsTrue();
        await Assert
            .That(module.Functions.Any(f => f.Name == "Program.Wrap__g1_6_ushort"))
            .IsTrue();

        // b=5, u=1000 -> u>>3=125 -> 5+125=130. Wrong if either instantiation mis-widths its argument
        // (e.g. the ushort path truncated through a byte-sized parameter slot).
        await Assert.That(RunBgp(GenericTransitiveSource, level)).IsEqualTo((byte)130);
    }

    [Test]
    public async Task GenericMethod_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debug = RunBgp(GenericTransitiveSource, OptimizationLevel.Debug);
        var release = RunBgp(GenericTransitiveSource, OptimizationLevel.Release);
        await Assert.That(release).IsEqualTo(debug);
    }

    // ---- Fixture 2: long/ulong arithmetic round trip --------------------------------------------
    //
    // Exercises ldc.i8 (a 64-bit literal), conv.i8 (widening an int local into a long), and
    // add/sub/mul/div/rem on i64 — none of which phase 1 wired up (Div/Rem had no opcode case at all,
    // for ANY width, and ldc.i8/conv.i8/conv.u8 were simply missing). The expected value is computed by
    // the SAME formula in plain C# right here, not a hand-derived magic number, so it can't itself hide
    // an arithmetic mistake.
    private const string LongArithmeticSource = """
        public class Program
        {
            public static long Main()
            {
                int seed = 5;
                long a = seed; // conv.i8
                long b = 1234567890123L; // ldc.i8
                long sum = a + b;
                long diff = b - a;
                long prod = a * 1000000L;
                long q = b / 1000000007L;
                long rem = b % 1000000007L;
                return sum + diff + prod + q + rem;
            }
        }
        """;

    private static long ExpectedLongArithmetic()
    {
        long seed = 5;
        long a = seed;
        long b = 1234567890123L;
        long sum = a + b;
        long diff = b - a;
        long prod = a * 1000000L;
        long q = b / 1000000007L;
        long rem = b % 1000000007L;
        return sum + diff + prod + q + rem;
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task LongArithmetic_AddSubMulDivRem_MatchesHostComputedValue(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(LongArithmeticSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var expected = unchecked((ulong)ExpectedLongArithmetic());
        await Assert.That(RunI64(LongArithmeticSource, level)).IsEqualTo(expected);
    }

    // ---- Fixture 2b: conv.i8 (sign-extend) vs conv.u8 (zero-extend) must extend DIFFERENTLY --------
    //
    // LongArithmeticSource's own conv.i8 (`long a = seed;`, seed=5) doesn't discriminate SExt from
    // ZExt (sign-extending a positive value is a no-op, same as zero-extending it) — this fixture picks
    // operands whose top bit is set, so a mixed-up SExt/ZExt produces a different, checkable result:
    // conv.i8 on a negative int must sign-extend (-5 stays -5, not 4294967291), and conv.u8 on a uint
    // with its top bit set must zero-extend (0x80000000 becomes +2147483648, not the sign-extended
    // negative value).
    private const string ConvI8U8Source = """
        public class Program
        {
            public static long Main()
            {
                int n = -5; // conv.i8 must sign-extend
                uint u = 0x80000000; // conv.u8 must zero-extend
                long a = n;
                long b = u;
                return a * 1000000L + b;
            }
        }
        """;

    private static long ExpectedConvI8U8()
    {
        int n = -5;
        uint u = 0x80000000;
        long a = n;
        long b = u;
        return a * 1000000L + b;
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task ConvI8SignExtends_ConvU8ZeroExtends_MatchesHostComputedValue(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(ConvI8U8Source, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var expected = unchecked((ulong)ExpectedConvI8U8());
        await Assert.That(RunI64(ConvI8U8Source, level)).IsEqualTo(expected);
    }

    // ---- Fixture 3: unsigned (ulong) division/remainder must be LOGICAL, not arithmetic ---------
    //
    // 'big' has its top bit set, so treating Div/Rem as signed (SDiv/SRem, real CLR two's-complement
    // int64) instead of unsigned (UDiv/URem) produces a wildly different, easily distinguished result.
    private const string UlongDivisionSource = """
        public class Program
        {
            public static ulong Main()
            {
                ulong big = 0xF000000000000000UL;
                ulong quotient = big / 16UL;
                ulong remainder = big % 16UL;
                return quotient + remainder;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task UlongDivision_IsUnsigned_NotArithmeticShift(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(UlongDivisionSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        const ulong big = 0xF000000000000000UL;
        const ulong expected = big / 16UL + big % 16UL; // 0x0F00000000000000 + 0
        await Assert.That(RunI64(UlongDivisionSource, level)).IsEqualTo(expected);
    }

    [Test]
    public async Task WideIntArithmetic_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debugLong = RunI64(LongArithmeticSource, OptimizationLevel.Debug);
        var releaseLong = RunI64(LongArithmeticSource, OptimizationLevel.Release);
        await Assert.That(releaseLong).IsEqualTo(debugLong);

        var debugConv = RunI64(ConvI8U8Source, OptimizationLevel.Debug);
        var releaseConv = RunI64(ConvI8U8Source, OptimizationLevel.Release);
        await Assert.That(releaseConv).IsEqualTo(debugConv);

        var debugUlong = RunI64(UlongDivisionSource, OptimizationLevel.Debug);
        var releaseUlong = RunI64(UlongDivisionSource, OptimizationLevel.Release);
        await Assert.That(releaseUlong).IsEqualTo(debugUlong);
    }
}
