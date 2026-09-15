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
/// Milestone M2 of the ideal-game-API program
/// (<c>docs/superpowers/specs/2026-07-19-ideal-game-api-design.md</c>): struct RETURN BY VALUE,
/// lowered as a hidden trailing sret pointer parameter (<c>CilLoweringContext.EnsureSignature</c>).
/// The north-star sample's <c>Board.ReadLine</c> returns a <c>Line</c> struct; these fixtures prove
/// the whole convention on the emulator — static returns, instance-method returns, factory methods,
/// call chaining, returns inside loops, recursion through a struct-returning function, a
/// monomorphized generic struct return, and the pattern-based <c>foreach</c> over a concrete struct
/// enumerator that struct returns newly unlock (Roslyn binds foreach structurally; a struct
/// enumerator's members are non-virtual <c>call</c>s, so <c>GetEnumerator()</c>'s struct return was
/// the ONLY missing piece). Verdicts cross out through SCY, completion through SCX, per the repo's
/// register-verdict pattern.
/// </summary>
public class CilStructReturnTests
{
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
        "koh-cil-struct-return-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilStructReturnAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_sret_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    private static GameBoySystem Compile(string source, OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var assemblyPath = CompileToAssembly(source, level);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        var errors = IrVerifier.Verify(module);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "IR verification failed:\n  " + string.Join("\n  ", errors)
            );
        IrOptimizer.Optimize(module);
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var link = new LinkerType().Link([new LinkerInput("cil", model)]);
        var rom =
            link.RomData
            ?? throw new InvalidOperationException(
                "no ROM; linker diagnostics:\n  "
                    + string.Join("\n  ", link.Diagnostics.Select(d => d.Message))
            );
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = 0x100;
        return gb;
    }

    private static void Run(GameBoySystem gb, int stepBudget = 2_000_000)
    {
        for (int steps = 0; steps < stepBudget; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < 0x100 || pc >= 0x8000)
                return;
            gb.StepInstruction();
        }
        throw new InvalidOperationException("program did not finish within the step budget");
    }

    private static async Task AssertPasses(string source, OptimizationLevel level)
    {
        var gb = Compile(source, level);
        Run(gb);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE); // completion marker
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1); // verdict
    }

    // ============================================================================================
    // Fixture 1: the north-star Board.ReadLine shape — a 4-byte struct built and RETURNED by a
    // helper, consumed via indexer instance methods, mutated, and passed back byval. Exercises:
    // static struct return, `default` init, chained instance calls on the returned value, and a
    // second return feeding a byval parameter.
    // ============================================================================================

    private const string LineSource = """
        using Koh.GameBoy;

        struct Line
        {
            private byte _a, _b, _c, _d;

            public byte Get(int i) => i == 0 ? _a : i == 1 ? _b : i == 2 ? _c : _d;

            public void Set(int i, byte v)
            {
                if (i == 0) _a = v;
                else if (i == 1) _b = v;
                else if (i == 2) _c = v;
                else _d = v;
            }

            public int Sum() => _a + _b + _c + _d;
        }

        public class Program
        {
            static Line Make(byte a, byte b, byte c, byte d)
            {
                Line line = default;
                line.Set(0, a);
                line.Set(1, b);
                line.Set(2, c);
                line.Set(3, d);
                return line;
            }

            static Line Reverse(Line line)
            {
                Line result = default;
                for (int i = 0; i < 4; i++)
                    result.Set(i, line.Get(3 - i));
                return result;
            }

            public static void Main()
            {
                byte ok = 1;

                Line l = Make(10, 20, 30, 40);
                if (l.Get(0) != 10) ok = 0;
                if (l.Get(3) != 40) ok = 0;
                if (l.Sum() != 100) ok = 0;

                Line r = Reverse(l);                  // struct return FED BY a byval struct arg
                if (r.Get(0) != 40) ok = 0;
                if (r.Get(3) != 10) ok = 0;

                l.Set(0, 99);                          // the two values are independent copies
                if (r.Get(3) != 10) ok = 0;
                if (Make(1, 2, 3, 4).Sum() != 10) ok = 0;   // chained: call on the returned value

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task StructReturn_BuildConsumeReverseAndChain(OptimizationLevel level) =>
        await AssertPasses(LineSource, level);

    // ============================================================================================
    // Fixture 2: instance methods and factories returning structs; a struct return used inside a
    // loop (the call-site buffer is reused per iteration); struct return stored into a STATIC
    // struct field (composes with M1's struct statics).
    // ============================================================================================

    private const string FactorySource = """
        using Koh.GameBoy;

        struct Point
        {
            public short X, Y;

            public static Point Make(short x, short y)
            {
                Point p = default;
                p.X = x;
                p.Y = y;
                return p;
            }

            public Point Offset(short dx, short dy) => Make((short)(X + dx), (short)(Y + dy));
        }

        public class Program
        {
            static Point _cursor;   // static struct field receiving a returned struct (stsfld copy)

            public static void Main()
            {
                byte ok = 1;

                _cursor = Point.Make(100, -50);
                if (_cursor.X != 100) ok = 0;
                if (_cursor.Y != -50) ok = 0;

                Point moved = _cursor.Offset(10, 60);   // instance method returning a struct
                if (moved.X != 110) ok = 0;
                if (moved.Y != 10) ok = 0;
                if (_cursor.X != 100) ok = 0;           // receiver untouched

                Point sum = Point.Make(0, 0);
                for (short i = 1; i <= 5; i++)
                    sum = sum.Offset(i, (short)(i * 2)); // returned into the same local each pass
                if (sum.X != 15) ok = 0;
                if (sum.Y != 30) ok = 0;

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task StructReturn_FactoriesInstanceMethodsLoopsAndStatics(
        OptimizationLevel level
    ) => await AssertPasses(FactorySource, level);

    // ============================================================================================
    // Fixture 3: recursion through a struct-returning function — the sret pointer is an ordinary
    // arg through the recursive ArgScratch convention; each depth's buffer lives in its caller's
    // (saved/restored) frame.
    // ============================================================================================

    private const string RecursionSource = """
        using Koh.GameBoy;

        struct Pair
        {
            public ushort Lo, Hi;
        }

        public class Program
        {
            // fib(n) and fib(n+1) in one struct, computed recursively.
            static Pair Fib(byte n)
            {
                Pair p = default;
                if (n == 0)
                {
                    p.Lo = 0;
                    p.Hi = 1;
                    return p;
                }
                Pair prev = Fib((byte)(n - 1));
                p.Lo = prev.Hi;
                p.Hi = (ushort)(prev.Lo + prev.Hi);
                return p;
            }

            public static void Main()
            {
                byte ok = 1;
                Pair f10 = Fib(10);
                if (f10.Lo != 55) ok = 0;    // fib(10)
                if (f10.Hi != 89) ok = 0;    // fib(11)
                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task StructReturn_ThroughRecursion(OptimizationLevel level) =>
        await AssertPasses(RecursionSource, level);

    // ============================================================================================
    // Fixture 4: monomorphized STATIC GENERIC method returning a struct (BuildGenericSignature's
    // sret path).
    // ============================================================================================

    private const string GenericSource = """
        using Koh.GameBoy;

        struct Box
        {
            public int Value;
            public byte Tag;
        }

        public class Program
        {
            // Monomorphized per T; T itself only shapes the signature (a type-pattern on T would
            // emit 'box', which is out of subset by design).
            static Box Tagged<T>(T ignored, int v, byte tag)
            {
                Box b = default;
                b.Value = v;
                b.Tag = tag;
                return b;
            }

            public static void Main()
            {
                byte ok = 1;
                Box a = Tagged<byte>(0, 200, 1);
                Box c = Tagged<ushort>(0, 40000, 2);
                if (a.Value != 200 || a.Tag != 1) ok = 0;
                if (c.Value != 40000 || c.Tag != 2) ok = 0;
                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task StructReturn_FromMonomorphizedGenericMethod(OptimizationLevel level) =>
        await AssertPasses(GenericSource, level);

    // ============================================================================================
    // Fixture 5: the foreach payoff — a concrete struct enumerator (pattern-based foreach, zero
    // interfaces) over a fixed range, and one over a static array of structs. GetEnumerator()
    // returns the enumerator struct BY VALUE: the exact member the sret convention unlocks.
    // ============================================================================================

    private const string ForeachSource = """
        using Koh.GameBoy;

        struct Range4
        {
            public RangeEnumerator GetEnumerator()
            {
                RangeEnumerator e = default;
                e.Index = -1;
                return e;
            }
        }

        struct RangeEnumerator
        {
            public int Index;

            public bool MoveNext()
            {
                Index++;
                return Index < 4;
            }

            public int Current => Index * 10;
        }

        public class Program
        {
            public static void Main()
            {
                byte ok = 1;

                int sum = 0;
                Range4 range = default;
                foreach (int v in range)          // 0 + 10 + 20 + 30
                    sum += v;
                if (sum != 60) ok = 0;

                int count = 0;
                foreach (int v in range)          // a SECOND foreach: fresh enumerator each time
                    count++;
                if (count != 4) ok = 0;

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task StructReturn_UnlocksPatternBasedForeach(OptimizationLevel level) =>
        await AssertPasses(ForeachSource, level);
}
