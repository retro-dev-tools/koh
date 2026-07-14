using System.Collections.Immutable;
using System.Globalization;
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
/// float32/float64 IL routing (see <c>CilMethodLowerer.Floats.cs</c>): <c>ldc.r4</c>/<c>ldc.r8</c>,
/// <c>conv.r4</c>/<c>conv.r8</c>, arithmetic, and compares on a real, compiled C# <c>float</c>/
/// <c>double</c> program, each routed to a <c>Koh.GameBoy.SoftFloat</c> <c>[KohRuntime]</c> routine.
///
/// Every arithmetic/conversion fixture verifies its own result INSIDE the compiled program (bit-for-bit
/// against a HOST-computed IEEE bit pattern baked into the generated source as a literal — the exact
/// pattern <c>CilKohRuntimeTests</c> already proved out for calling <c>SoftFloat.AddF32</c> directly),
/// then reports one pass/fail byte via <c>Hardware.SCX</c> (completion via <c>Hardware.SCY</c>, so a
/// program that crashes before finishing can't misread as a false pass). This avoids needing any
/// wide (&gt;1 byte) result read back through the emulator's own register/memory surface — the
/// comparison itself is real Koh IR (an ordinary <c>ceq</c> on the raw bits), so a wrong float op would
/// still fail this exactly as visibly as an external byte-for-byte comparison would.
/// </summary>
public class CilFloatTests
{
    // ---- Roslyn: compile real C# to a real assembly on disk ------------------------------------

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
        "koh-cil-float-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilFloatAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true // every fixture below reads its own result's raw bits via `*(uint*)&x`
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_float_{Guid.NewGuid():N}.dll");
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

    // ---- Emulator harness (mirrors CilKohRuntimeTests/CilEndToEndTests) -------------------------

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

    /// <summary>Runs <paramref name="source"/> and returns (verdict byte off SCX, completion marker off
    /// SCY) — every fixture in this file ends with exactly this two-register report.</summary>
    private static (byte Verdict, byte Completed) RunVerdict(string source, OptimizationLevel level)
    {
        var gb = Load(Compile(source, level), out int s, out int l);
        Run(gb, s, l);
        return (gb.DebugReadByte(0xFF43), gb.DebugReadByte(0xFF42)); // SCX, SCY
    }

    private static async Task AssertPasses(string source, OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(source, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var (verdict, completed) = RunVerdict(source, level);
        await Assert.That(completed).IsEqualTo((byte)0xEE); // Main ran to its natural end
        await Assert.That(verdict).IsEqualTo((byte)1);
    }

    // ---- Literal/hex formatting -------------------------------------------------------------------

    /// <summary>A round-trippable C# `float` literal (G9 round-trips single precision).</summary>
    private static string LitF32(float x) => x.ToString("G9", CultureInfo.InvariantCulture) + "f";

    /// <summary>A round-trippable C# `double` literal (G17 round-trips double precision).</summary>
    private static string LitF64(double x) => x.ToString("G17", CultureInfo.InvariantCulture);

    private static string HexU32(uint bits) => "0x" + bits.ToString("X8");

    private static string HexU64(ulong bits) => "0x" + bits.ToString("X16") + "UL";

    // ============================================================================================
    // f32 arithmetic: add/sub/mul/div, each routed to Koh.GameBoy.SoftFloat's [KohRuntime("f32.*")]
    // routine, verified bit-for-bit against real .NET float arithmetic.
    // ============================================================================================

    private static string F32ArithSource(string op, float x, float y, float expected) =>
        $$"""
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    float a = {{LitF32(x)}};
                    float b = {{LitF32(y)}};
                    float c = a {{op}} b;
                    uint bits = *(uint*)&c;
                    Hardware.SCX = (byte)(bits == {{HexU32(
                BitConverter.SingleToUInt32Bits(expected)
            )}}u ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;

    [Test]
    [Arguments("+", 1.5f, 2.5f, OptimizationLevel.Debug)]
    [Arguments("+", 1.5f, 2.5f, OptimizationLevel.Release)]
    [Arguments("+", 0.1f, 0.2f, OptimizationLevel.Debug)] // round-to-nearest-even
    [Arguments("-", 3.5f, 1.25f, OptimizationLevel.Debug)]
    [Arguments("-", 1.0f, 4.0f, OptimizationLevel.Debug)] // negative result
    [Arguments("*", 2.0f, 3.0f, OptimizationLevel.Debug)]
    [Arguments("*", -1.5f, 4.0f, OptimizationLevel.Debug)]
    [Arguments("/", 7.0f, 2.0f, OptimizationLevel.Debug)]
    [Arguments("/", 1.0f, 3.0f, OptimizationLevel.Debug)] // non-terminating -> rounding
    public async Task F32_Arithmetic_MatchesHost(
        string op,
        float x,
        float y,
        OptimizationLevel level
    )
    {
        float expected = op switch
        {
            "+" => x + y,
            "-" => x - y,
            "*" => x * y,
            "/" => x / y,
            _ => throw new ArgumentException(op),
        };
        await AssertPasses(F32ArithSource(op, x, y, expected), level);
    }

    [Test]
    public async Task F32_Add_RoutesThroughRealSoftFloatFunction()
    {
        // The routine actually lowered into the module under its real name (proving the `+` operator
        // resolved into Koh.GameBoy.SoftFloat.AddF32, not a diagnostic-swallowed or silently-wrong path).
        var module = Frontend(
            F32ArithSource("+", 1.5f, 2.5f, 4.0f),
            OptimizationLevel.Debug,
            new DiagnosticBag()
        );
        await Assert.That(module.Functions.Select(f => f.Name)).Contains("SoftFloat.AddF32");
    }

    // ============================================================================================
    // f32 compares: ceq/clt/cgt (ordered) routed to Koh.GameBoy.SoftFloat's bool routines.
    // ============================================================================================

    private static string F32CompareSource(string op, float x, float y, bool expected) =>
        $$"""
            using Koh.GameBoy;

            public class Program
            {
                public static void Main()
                {
                    float a = {{LitF32(x)}};
                    float b = {{LitF32(y)}};
                    bool r = a {{op}} b;
                    Hardware.SCX = (byte)(r == {{(expected ? "true" : "false")}} ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;

    [Test]
    [Arguments("<", 1.5f, 2.0f)]
    [Arguments("<", 2.0f, 1.5f)]
    [Arguments("<=", 2.0f, 2.0f)]
    [Arguments(">", -1.0f, -2.0f)]
    [Arguments(">=", 1.0f, 1.0f)]
    [Arguments("==", 0.1f, 0.1f)]
    [Arguments("!=", 0.1f, 0.2f)]
    [Arguments("<", -0.0f, 0.0f)] // -0.0 == 0.0 -> not <
    public async Task F32_Compare_MatchesHost(string op, float x, float y)
    {
        bool expected = op switch
        {
            "<" => x < y,
            "<=" => x <= y,
            ">" => x > y,
            ">=" => x >= y,
            "==" => x == y,
            "!=" => x != y,
            _ => throw new ArgumentException(op),
        };
        await AssertPasses(F32CompareSource(op, x, y, expected), OptimizationLevel.Debug);
        await AssertPasses(F32CompareSource(op, x, y, expected), OptimizationLevel.Release);
    }

    // ============================================================================================
    // A float relational used DIRECTLY as a branch condition (`if (a < b)`) — Roslyn compiles this to
    // a fused branch-compare opcode (`blt`/`ble`/`bgt`/`bge`/`beq`/`bne.un` and their `.un` forms), NOT
    // to `clt`/`cgt`/`ceq` followed by `brtrue`/`brfalse` — a genuinely separate interception point
    // from F32_Compare_MatchesHost above (see CilMethodLowerer.Floats.cs's TryFloatBranchCondition).
    // Negative operands are deliberately included: comparing the raw IEEE bit patterns as ordinary
    // SIGNED integers (the bug this exercises) gets a negative-vs-negative comparison backwards.
    // ============================================================================================

    [Test]
    [Arguments("<", 1.5f, 2.0f)]
    [Arguments("<", 2.0f, 1.5f)]
    [Arguments("<", -1.0f, -2.0f)] // negatives: integer-bits comparison would get this backwards
    [Arguments("<", -2.0f, -1.0f)]
    [Arguments(">=", -1.0f, -1.0f)]
    [Arguments("==", 0.1f, 0.1f)]
    [Arguments("!=", -0.5f, -0.5f)]
    public async Task F32_CompareInBranchPosition_MatchesHost(string op, float x, float y)
    {
        bool expected = op switch
        {
            "<" => x < y,
            ">=" => x >= y,
            "==" => x == y,
            "!=" => x != y,
            _ => throw new ArgumentException(op),
        };
        string source = $$"""
            using Koh.GameBoy;

            public class Program
            {
                public static void Main()
                {
                    float a = {{LitF32(x)}};
                    float b = {{LitF32(y)}};
                    int result;
                    if (a {{op}} b)
                        result = 1;
                    else
                        result = 0;
                    Hardware.SCX = (byte)(result == {{(expected ? 1 : 0)}} ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, OptimizationLevel.Debug);
        await AssertPasses(source, OptimizationLevel.Release);
    }

    // ============================================================================================
    // `a < b ? a : b` (a "min" idiom): the float relational feeds a `?:`'s condition (branch position
    // again) AND its two arms join at a control-flow spill/reload (see CilMethodLowerer.cs's Deliver/
    // EntryStack and _spillFloatKind) — the result must still be routable through more float arithmetic
    // (here, just read back), not silently lose its float tag across the join.
    // ============================================================================================

    [Test]
    [Arguments(1.5f, 2.5f)]
    [Arguments(2.5f, 1.5f)]
    [Arguments(-1.0f, -2.0f)] // negatives: also exercises the branch-compare fix above
    public async Task F32_TernaryMin_MatchesHost(float x, float y)
    {
        // The join result feeds MORE float arithmetic (`+ 1.0f`), not just a bit readback — a reload
        // that silently lost its float tag (see _spillFloatKind's remarks) would route this `+` through
        // the ordinary int path instead of SoftFloat, giving a wrong answer this catches.
        float expected = (x < y ? x : y) + 1.0f;
        string source = $$"""
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    float a = {{LitF32(x)}};
                    float b = {{LitF32(y)}};
                    float m = (a < b ? a : b) + 1.0f;
                    uint bits = *(uint*)&m;
                    Hardware.SCX = (byte)(bits == {{HexU32(
                BitConverter.SingleToUInt32Bits(expected)
            )}}u ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, OptimizationLevel.Debug);
        await AssertPasses(source, OptimizationLevel.Release);
    }

    // ============================================================================================
    // Unary `neg`: TryFloatNeg routes to "f32.neg"/"f64.neg". Negating a LOCAL (not a literal
    // constant, which Roslyn would just constant-fold to a different `ldc.r4`) is required to actually
    // emit a `neg` opcode in Debug IL — mirrors F32_Add_RoutesThroughRealSoftFloatFunction's own
    // "Debug doesn't fold locals" reasoning.
    // ============================================================================================

    [Test]
    [Arguments(1.5f)]
    [Arguments(-2.5f)]
    [Arguments(0.0f)]
    public async Task F32_Neg_MatchesHost(float x)
    {
        float expected = -x;
        string source = $$"""
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    float a = {{LitF32(x)}};
                    float c = -a;
                    uint bits = *(uint*)&c;
                    Hardware.SCX = (byte)(bits == {{HexU32(
                BitConverter.SingleToUInt32Bits(expected)
            )}}u ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, OptimizationLevel.Debug);
        await AssertPasses(source, OptimizationLevel.Release);
    }

    [Test]
    public async Task F32_Neg_RoutesThroughRealSoftFloatFunction()
    {
        const string source = """
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    float a = 1.5f;
                    float c = -a;
                    uint bits = *(uint*)&c;
                    Hardware.SCX = (byte)(bits == 0xBFC00000u ? 1 : 0); // -1.5f
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        var module = Frontend(source, OptimizationLevel.Debug, new DiagnosticBag());
        await Assert.That(module.Functions.Select(f => f.Name)).Contains("SoftFloat.NegF32");
    }

    // ============================================================================================
    // Conversions: int<->float32.
    // ============================================================================================

    [Test]
    [Arguments(3.7f, 3)]
    [Arguments(-3.7f, -3)] // truncates toward zero
    [Arguments(1000.25f, 1000)]
    public async Task F32_ToInt_MatchesHost(float x, int expected)
    {
        string source = $$"""
            using Koh.GameBoy;

            public class Program
            {
                public static void Main()
                {
                    float f = {{LitF32(x)}};
                    int i = (int)f;
                    Hardware.SCX = (byte)(i == {{expected}} ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, OptimizationLevel.Debug);
        await AssertPasses(source, OptimizationLevel.Release);
    }

    [Test]
    [Arguments(0)]
    [Arguments(5)]
    [Arguments(-5)]
    [Arguments(1000000)]
    [Arguments(16777217)] // 2^24 + 1: not exactly representable -> tests rounding
    public async Task F32_FromInt_MatchesHost(int x)
    {
        uint expectedBits = BitConverter.SingleToUInt32Bits((float)x);
        string source = $$"""
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    int i = {{x}};
                    float f = (float)i;
                    uint bits = *(uint*)&f;
                    Hardware.SCX = (byte)(bits == {{HexU32(expectedBits)}}u ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, OptimizationLevel.Debug);
        await AssertPasses(source, OptimizationLevel.Release);
    }

    // ============================================================================================
    // Cross-width conversions: float32<->float64, and float64<->int. Each of these composes through a
    // DIFFERENT [KohRuntime] key than the int<->float32 tests above ("f32.toF64"/"f64.toF32" directly;
    // "f64.toI64" via ResolveFloatToInt's documented int32-target composition — see
    // CilMethodLowerer.Floats.cs's remarks) — every one of those keys is otherwise never resolved by
    // any other fixture in this file, so this is the only place a mistyped key or a broken composition
    // would surface. A LOCAL (not a literal) source is required so Roslyn actually emits a `conv.r4`/
    // `conv.r8` opcode in Debug IL rather than constant-folding the whole expression away — same
    // discipline as F32_Neg_MatchesHost/F32_Add_RoutesThroughRealSoftFloatFunction.
    // ============================================================================================

    [Test]
    [Arguments(1.5f)]
    [Arguments(-2.5f)]
    [Arguments(0.1f)] // not exactly representable in either width -> exercises real widening, not a no-op
    public async Task F32_ToF64_MatchesHost(float x)
    {
        ulong expectedBits = BitConverter.DoubleToUInt64Bits((double)x);
        string source = $$"""
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    float a = {{LitF32(x)}};
                    double d = a;
                    ulong bits = *(ulong*)&d;
                    Hardware.SCX = (byte)(bits == {{HexU64(expectedBits)}} ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, OptimizationLevel.Debug);
        await AssertPasses(source, OptimizationLevel.Release);
    }

    [Test]
    public async Task F32_ToF64_RoutesThroughRealSoftFloatFunction()
    {
        const string source = """
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    float a = 1.5f;
                    double d = a;
                    ulong bits = *(ulong*)&d;
                    Hardware.SCX = (byte)(bits == 0x3FF8000000000000UL ? 1 : 0); // (double)1.5f
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        var module = Frontend(source, OptimizationLevel.Debug, new DiagnosticBag());
        await Assert.That(module.Functions.Select(f => f.Name)).Contains("SoftFloat.ToF64");
    }

    [Test]
    [Arguments(1.5)]
    [Arguments(-2.5)]
    [Arguments(0.1)] // rounds down when narrowed -> exercises SoftFloat.ToF32's real rounding logic
    public async Task F64_ToF32_MatchesHost(double x)
    {
        uint expectedBits = BitConverter.SingleToUInt32Bits((float)x);
        string source = $$"""
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    double a = {{LitF64(x)}};
                    float f = (float)a;
                    uint bits = *(uint*)&f;
                    Hardware.SCX = (byte)(bits == {{HexU32(expectedBits)}}u ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, OptimizationLevel.Debug);
        await AssertPasses(source, OptimizationLevel.Release);
    }

    [Test]
    public async Task F64_ToF32_RoutesThroughRealSoftFloatFunction()
    {
        const string source = """
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    double a = 1.5;
                    float f = (float)a;
                    uint bits = *(uint*)&f;
                    Hardware.SCX = (byte)(bits == 0x3FC00000u ? 1 : 0); // (float)1.5
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        var module = Frontend(source, OptimizationLevel.Debug, new DiagnosticBag());
        await Assert.That(module.Functions.Select(f => f.Name)).Contains("SoftFloat.ToF32");
    }

    [Test]
    [Arguments(3.7)]
    [Arguments(-3.7)]
    [Arguments(1000.25)]
    public async Task F64_ToInt_MatchesHost(double x)
    {
        int expected = (int)x;
        string source = $$"""
            using Koh.GameBoy;

            public class Program
            {
                public static void Main()
                {
                    double a = {{LitF64(x)}};
                    int i = (int)a;
                    Hardware.SCX = (byte)(i == {{expected}} ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, OptimizationLevel.Debug);
        await AssertPasses(source, OptimizationLevel.Release);
    }

    // 16777217 (2^24 + 1) is exactly representable in f64 but NOT f32 (float32's 24-bit mantissa rounds
    // it to 16777216.0f): catches ConvertIntToF64 composing through float32 first (a double-rounding
    // bug), since a direct int32->f64 conversion (via int64->f64, a single rounding step) is exact.
    [Test]
    [Arguments(0)]
    [Arguments(5)]
    [Arguments(-5)]
    [Arguments(1000000)]
    [Arguments(16777217)]
    [Arguments(-16777217)]
    public async Task F64_FromInt_MatchesHost(int x)
    {
        ulong expectedBits = BitConverter.DoubleToUInt64Bits((double)x);
        string source = $$"""
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    int i = {{x}};
                    double d = i;
                    ulong bits = *(ulong*)&d;
                    Hardware.SCX = (byte)(bits == {{HexU64(expectedBits)}} ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, OptimizationLevel.Debug);
        await AssertPasses(source, OptimizationLevel.Release);
    }

    // An UNSIGNED source (`uint`) routes through `conv.r.un` first (see LowerConvRUn), a genuinely
    // separate opcode path from F64_FromInt_MatchesHost's plain (signed) `conv.r8` above. 16777217u is
    // the same discriminating value: exact int32(unsigned)->f64 needs a single rounding step (via
    // int64), not a double-rounding composition through float32.
    [Test]
    [Arguments(0u)]
    [Arguments(5u)]
    [Arguments(1000000u)]
    [Arguments(16777217u)]
    public async Task F64_FromUInt_MatchesHost(uint x)
    {
        ulong expectedBits = BitConverter.DoubleToUInt64Bits((double)x);
        string source = $$"""
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    uint u = {{x}}u;
                    double d = u;
                    ulong bits = *(ulong*)&d;
                    Hardware.SCX = (byte)(bits == {{HexU64(expectedBits)}} ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, OptimizationLevel.Debug);
        await AssertPasses(source, OptimizationLevel.Release);
    }

    // ============================================================================================
    // f64 arithmetic/compare: the same routing, at double width ("f64.*" keys).
    // ============================================================================================

    private static string F64ArithSource(string op, double x, double y, double expected) =>
        $$"""
            using Koh.GameBoy;

            public unsafe class Program
            {
                public static void Main()
                {
                    double a = {{LitF64(x)}};
                    double b = {{LitF64(y)}};
                    double c = a {{op}} b;
                    ulong bits = *(ulong*)&c;
                    Hardware.SCX = (byte)(bits == {{HexU64(
                BitConverter.DoubleToUInt64Bits(expected)
            )}} ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;

    [Test]
    [Arguments("+", 1.25, 3.5)]
    [Arguments("-", 5.0, 1.5)]
    [Arguments("*", 2.0, 3.25)]
    [Arguments("/", 7.0, 2.0)]
    public async Task F64_Arithmetic_MatchesHost(string op, double x, double y)
    {
        double expected = op switch
        {
            "+" => x + y,
            "-" => x - y,
            "*" => x * y,
            "/" => x / y,
            _ => throw new ArgumentException(op),
        };
        await AssertPasses(F64ArithSource(op, x, y, expected), OptimizationLevel.Debug);
        await AssertPasses(F64ArithSource(op, x, y, expected), OptimizationLevel.Release);
    }

    [Test]
    [Arguments("<", 1.5, 2.0)]
    [Arguments(">=", 2.0, 2.0)]
    [Arguments("==", 0.1, 0.1)]
    [Arguments("!=", 0.1, 0.2)]
    public async Task F64_Compare_MatchesHost(string op, double x, double y)
    {
        bool expected = op switch
        {
            "<" => x < y,
            ">=" => x >= y,
            "==" => x == y,
            "!=" => x != y,
            _ => throw new ArgumentException(op),
        };
        string source = $$"""
            using Koh.GameBoy;

            public class Program
            {
                public static void Main()
                {
                    double a = {{LitF64(x)}};
                    double b = {{LitF64(y)}};
                    bool r = a {{op}} b;
                    Hardware.SCX = (byte)(r == {{(expected ? "true" : "false")}} ? 1 : 0);
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, OptimizationLevel.Debug);
        await AssertPasses(source, OptimizationLevel.Release);
    }

    // ============================================================================================
    // ref float: `ldind.r4`/`stind.r4` (Ldind_R4/Stind_R4 — see CilMethodLowerer.cs's opcode switch)
    // via a real byref float parameter, not just a plain local.
    // ============================================================================================

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task F32_RefParameter_ReadsAndWritesThroughIndirection(OptimizationLevel level)
    {
        const string source = """
            using Koh.GameBoy;

            public unsafe class Program
            {
                static void AddInPlace(ref float a, float b)
                {
                    a = a + b;
                }

                public static void Main()
                {
                    float x = 1.5f;
                    AddInPlace(ref x, 2.5f);
                    uint bits = *(uint*)&x;
                    Hardware.SCX = (byte)(bits == 0x40800000u ? 1 : 0); // 1.5f + 2.5f == 4.0f
                    Hardware.SCY = 0xEE;
                }
            }
            """;
        await AssertPasses(source, level);
    }

    // ============================================================================================
    // A float op with no matching [KohRuntime] key ("rem" — see CilMethodLowerer.Floats.cs's class
    // remarks) is a DIAGNOSTIC naming the key, never a silent int-remainder-of-the-bits miscompile.
    // ============================================================================================

    [Test]
    public async Task F32_Rem_NoMatchingRuntimeKey_IsADiagnosticNotAMiscompile()
    {
        const string source = """
            using Koh.GameBoy;

            public class Program
            {
                public static void Main()
                {
                    float a = 5.5f;
                    float b = 2.0f;
                    float r = a % b;
                    Hardware.SCX = (byte)(r > 0 ? 1 : 0);
                }
            }
            """;
        var diagnostics = new DiagnosticBag();
        var assemblyPath = CompileToAssembly(source, OptimizationLevel.Debug);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        new CilFrontend().Lower(input, diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error)).IsTrue();
        await Assert.That(diagnostics.Any(d => d.Message.Contains("f32.rem"))).IsTrue();
    }

    // ============================================================================================
    // Debug vs. Release: the same source, compiled by Roslyn's two optimization levels to genuinely
    // different IL, must still lower to a ROM that computes the identical result.
    // ============================================================================================

    [Test]
    public async Task F32_Arithmetic_DebugAndReleaseProduceIdenticalObservableState()
    {
        var source = F32ArithSource("+", 0.1f, 0.2f, 0.1f + 0.2f);
        var debug = RunVerdict(source, OptimizationLevel.Debug);
        var release = RunVerdict(source, OptimizationLevel.Release);
        await Assert.That(release).IsEqualTo(debug);
        await Assert.That(debug.Completed).IsEqualTo((byte)0xEE);
        await Assert.That(debug.Verdict).IsEqualTo((byte)1);
    }

    [Test]
    public async Task F64_Arithmetic_DebugAndReleaseProduceIdenticalObservableState()
    {
        var source = F64ArithSource("/", 1.0, 3.0, 1.0 / 3.0);
        var debug = RunVerdict(source, OptimizationLevel.Debug);
        var release = RunVerdict(source, OptimizationLevel.Release);
        await Assert.That(release).IsEqualTo(debug);
        await Assert.That(debug.Completed).IsEqualTo((byte)0xEE);
        await Assert.That(debug.Verdict).IsEqualTo((byte)1);
    }
}
