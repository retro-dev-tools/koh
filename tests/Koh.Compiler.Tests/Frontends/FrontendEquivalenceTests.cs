using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Frontends.CSharp;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// THE A/B oracle: the last chance to catch a <see cref="CilFrontend"/> miscompile against the
/// known-good <see cref="CSharpFrontend"/> reference before the C# frontend is deleted. For every
/// fixture below, the SAME real C# source is compiled twice — once through <see cref="CSharpFrontend"/>
/// directly from <see cref="SourceText"/>, once through Roslyn to a real assembly and then
/// <see cref="CilFrontend"/> — verified clean on both, run through the identical SM83 backend/linker/
/// emulator pipeline, and asserted to produce IDENTICAL observable state.
///
/// Observable state is deliberately narrow: the fixture's declared return value via the ABI both
/// frontends share (i8 -&gt; A, i16 -&gt; HL, i32 -&gt; DE:HL, i64 -&gt; <see cref="Sm83Backend.ReturnScratch"/>),
/// or a specific MMIO cell the fixture writes (Hardware.BGP/SCX/...). A blanket register/WRAM dump
/// would surface benign codegen differences (different function order/temporaries/WRAM layout between
/// the two frontends leaves different leftover values in unrelated registers/cells after RET) as false
/// "disagreements" — see this file's fixtures for the specific cells each one checks.
///
/// One difference between the frontends is intentional, not a bug: "Koh C#" (<see cref="CSharpFrontend"/>)
/// does NOT do C#'s integer promotion — `byte * 16` computes and wraps at byte width — while standard
/// C#/CIL (<see cref="CilFrontend"/>) promotes byte/short operands to `int` per the usual arithmetic
/// conversions before the multiply. Every AGREEMENT fixture below sidesteps this with explicit casts;
/// <see cref="DocumentedDivergence_IntPromotion_ByteTimesLiteral_FrontendsIntentionallyDisagree"/> is the
/// one fixture that deliberately exercises it and documents the (different, expected) result on each side.
/// </summary>
public class FrontendEquivalenceTests
{
    // ---- Roslyn: compile real C# to a real assembly on disk, for the CIL frontend -------------

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
        "koh-frontend-equivalence-tests"
    );

    private static string CompileToAssembly(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "FrontendEquivalenceAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true // several fixtures below poke pointers / stackalloc
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"asm_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    // ---- Frontend -> IR (verified) -> optimizer -> SM83 backend, once per frontend -------------

    private static IrModule LowerCil(string source, DiagnosticBag diagnostics)
    {
        var assemblyPath = CompileToAssembly(source);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        return new CilFrontend().Lower(input, diagnostics);
    }

    private static IrModule LowerCSharp(string source, DiagnosticBag diagnostics) =>
        new CSharpFrontend().Lower(SourceText.From(source, "game.cs"), diagnostics);

    /// <summary>Runs one frontend end to end: lower, assert no error diagnostics (a fixture BOTH
    /// frontends must host — an error here is a reportable finding, not a fixture to silently drop),
    /// assert the IR verifies clean, optimize (CompilerDriver's default path), and hand to the backend.
    /// Throwing (rather than swallowing) a frontend failure surfaces it loudly in the test output.</summary>
    private static EmitModel Compile(Func<string, DiagnosticBag, IrModule> lower, string source)
    {
        var diagnostics = new DiagnosticBag();
        var module = lower(source, diagnostics);
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
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    // ---- Emulator harness (mirrors CSharpEndToEndTests / CilEndToEndTests) --------------------

    private static GameBoySystem Load(EmitModel model, out int start, out int length)
    {
        var link = new LinkerType().Link([new LinkerInput("fe", model)]);
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

    private static GameBoySystem RunCil(string source)
    {
        var gb = Load(Compile(LowerCil, source), out int s, out int l);
        Run(gb, s, l);
        return gb;
    }

    private static GameBoySystem RunCSharp(string source)
    {
        var gb = Load(Compile(LowerCSharp, source), out int s, out int l);
        Run(gb, s, l);
        return gb;
    }

    // ---- Typed observables, matching the shared SM83 return ABI --------------------------------

    private static byte A(GameBoySystem gb) => gb.Registers.A;

    private static ushort HL(GameBoySystem gb) => gb.Registers.HL;

    private static uint I32(GameBoySystem gb) => ((uint)gb.Registers.DE << 16) | gb.Registers.HL;

    private static ulong I64(GameBoySystem gb)
    {
        ulong result = 0;
        for (int i = 0; i < 8; i++)
            result |= (ulong)gb.DebugReadByte((ushort)(Sm83Backend.ReturnScratch + i)) << (8 * i);
        return result;
    }

    private static byte Bgp(GameBoySystem gb) => gb.DebugReadByte(0xFF47); // Hardware.BGP

    /// <summary>Runs <paramref name="source"/> through both frontends, extracts the same typed
    /// observable from each, and asserts they agree AND match the independently-computed expected
    /// value — so a bug that happened to make both frontends wrong the same way still gets caught.</summary>
    private static async Task AssertAgree<T>(
        string source,
        Func<GameBoySystem, T> extract,
        T expected
    )
        where T : IEquatable<T>
    {
        var cil = extract(RunCil(source));
        var cs = extract(RunCSharp(source));
        await Assert.That(cs).IsEqualTo(cil);
        await Assert.That(cil).IsEqualTo(expected);
    }

    // =============================================================================================
    // Arithmetic at every width (i8/i16/i32/i64/i128), unsigned + signed, explicit casts throughout
    // so both frontends' usual-arithmetic-conversion rules agree (see class remarks on int promotion).
    // =============================================================================================

    [Test]
    public async Task Arithmetic_Byte_MulAndWrap() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static byte Main()
                {
                    byte a = 200;
                    byte b = 100;
                    return (byte)(a + b); // wraps mod 256
                }
            }
            """,
            A,
            (byte)44
        );

    [Test]
    public async Task Arithmetic_Sbyte_SignedDivide() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static byte Main()
                {
                    sbyte a = -17;
                    sbyte b = 5;
                    sbyte q = (sbyte)(a / b); // truncates toward zero -> -3
                    return (byte)q;
                }
            }
            """,
            A,
            unchecked((byte)-3)
        );

    [Test]
    public async Task Arithmetic_Ushort_ShiftAndMask() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static ushort Main()
                {
                    ushort x = 0xFF00;
                    ushort y = (ushort)(x >> 4);
                    return (ushort)(y & 0x0FFF);
                }
            }
            """,
            HL,
            (ushort)0x0FF0
        );

    [Test]
    public async Task Arithmetic_Int32_MultiplyAndAccumulate() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static int Main()
                {
                    int total = 0;
                    for (int i = 1; i <= 6; i++)
                        total += i * i;
                    return total; // 1+4+9+16+25+36 = 91
                }
            }
            """,
            I32,
            91u
        );

    [Test]
    public async Task Arithmetic_UInt32_BitwiseAcrossWords() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static uint Main()
                {
                    uint a = 0xFFFF0000;
                    uint b = 0x0000FFFF;
                    return a | b;
                }
            }
            """,
            I32,
            0xFFFFFFFFu
        );

    [Test]
    public async Task Arithmetic_Int64_MulDivRem()
    {
        long a = 1234567890123L;
        long b = 987654321L;
        long expected = a * 3L + a / b + a % b;
        await AssertAgree(
            $$"""
            public static class Program
            {
                public static long Main()
                {
                    long a = {{a}}L;
                    long b = {{b}}L;
                    return a * 3L + a / b + a % b;
                }
            }
            """,
            I64,
            unchecked((ulong)expected)
        );
    }

    [Test]
    public async Task Arithmetic_Int128_AddShiftDivide() =>
        await AssertAgree(
            """
            using System;
            using Koh.GameBoy;

            public static class Program
            {
                public static void Main()
                {
                    Int128 a = 123456789012345678;
                    Int128 b = 987654321098765432;
                    Int128 sum = a + b;
                    Int128 diff = sum - b; // == a
                    UInt128 ua = 250;
                    UInt128 ushifted = ua << 4; // 4000
                    bool ok = diff == a && ushifted == 4000;
                    Hardware.BGP = (byte)(ok ? 1 : 0);
                }
            }
            """,
            Bgp,
            (byte)1
        );

    // =============================================================================================
    // Control flow: for/while, switch jump table, break/continue.
    // =============================================================================================

    [Test]
    public async Task ControlFlow_ForLoopWithBreakAndContinue() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static byte Main()
                {
                    byte total = 0;
                    for (byte i = 0; i < 20; i++)
                    {
                        if (i == 15) break;
                        if ((i % 2) == 0) continue;
                        total = (byte)(total + i);
                    }
                    return total; // 1+3+5+7+9+11+13 = 49
                }
            }
            """,
            A,
            (byte)49
        );

    [Test]
    public async Task ControlFlow_SwitchJumpTable() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static byte Main()
                {
                    int selector = 6;
                    byte result;
                    switch (selector)
                    {
                        case 0: result = 10; break;
                        case 1: result = 11; break;
                        case 2: result = 12; break;
                        case 3: result = 13; break;
                        case 4: result = 14; break;
                        case 5: result = 15; break;
                        case 6: result = 66; break;
                        case 7: result = 17; break;
                        case 8: result = 18; break;
                        case 9: result = 19; break;
                        default: result = 99; break;
                    }
                    return result;
                }
            }
            """,
            A,
            (byte)66
        );

    // =============================================================================================
    // Structs: nested fields, whole-struct copy (no aliasing), struct array.
    // =============================================================================================

    // NOTE: this fixture deliberately declares `copy` first and assigns it separately (`Entity copy;
    // copy = e;`), NOT `Entity copy = e;` (declare-with-struct-initializer). The A/B run below caught a
    // genuine, PRE-EXISTING CSharpFrontend bug in the latter form — see this file's class remarks /
    // the task report for the full writeup — that is orthogonal to CilFrontend correctness (CilFrontend
    // handles `Entity copy = e;` correctly; CSharpFrontend's own `LowerLocalDeclaration` silently drops
    // the initializer for a named-struct-typed local, so `copy` starts uninitialized). Since
    // CSharpFrontend is scheduled for deletion this phase, that bug is reported, not fixed, and this
    // fixture uses the assignment form both frontends have always agreed on (CSharpEndToEndTests'
    // Struct_ValueCopy uses the same form) so it tests whole-struct-copy semantics without tripping over
    // an unrelated, already-dying-code defect.
    [Test]
    public async Task Structs_NestedFieldsAndWholeCopyDoNotAlias() =>
        await AssertAgree(
            """
            public struct Point { public byte X; public byte Y; }
            public struct Entity { public Point Pos; public byte Hp; }

            public static class Program
            {
                public static byte Main()
                {
                    Entity e;
                    e.Pos.X = 5;
                    e.Pos.Y = 9;
                    e.Hp = 100;
                    Entity copy;
                    copy = e;
                    copy.Pos.X = (byte)(copy.Pos.X + 50);
                    // e must be untouched by mutating copy.
                    return (byte)(e.Pos.X + e.Pos.Y + e.Hp + copy.Pos.X); // 5+9+100+55
                }
            }
            """,
            A,
            (byte)169
        );

    [Test]
    public async Task Structs_ArrayOfStructsIndependentElements() =>
        await AssertAgree(
            """
            public struct Point { public byte X; public byte Y; }

            public static class Program
            {
                public static byte Main()
                {
                    Point[] pts = new Point[4];
                    pts[0].X = 5;
                    pts[1].X = 6;
                    pts[2].X = 7;
                    return (byte)(pts[0].X + pts[1].X + pts[2].X + pts[3].X); // 5+6+7+0
                }
            }
            """,
            A,
            (byte)18
        );

    // =============================================================================================
    // Classes: heap allocation, instance fields/methods, independent instances.
    // =============================================================================================

    [Test]
    public async Task Classes_InstanceMethodsAndIndependentInstances() =>
        await AssertAgree(
            """
            public class Counter
            {
                public byte N;
                public void Add(byte v) { N = (byte)(N + v); }
                public byte Get() { return N; }
            }

            public static class Program
            {
                public static byte Main()
                {
                    Counter a = new Counter();
                    Counter b = new Counter();
                    a.Add(10);
                    a.Add(1);
                    b.Add(3);
                    return (byte)(a.Get() * 10 + b.Get()); // 11*10 + 3
                }
            }
            """,
            A,
            (byte)113
        );

    // A fixed 3-hop traversal (not a null-terminated walk): CSharpFrontend's class-typed fields are
    // plain heap pointers with no nullable-reference-type support (`Node?` is rejected as an
    // unsupported type), so this sidesteps that by walking a known-length chain instead of comparing
    // against null.
    [Test]
    public async Task Classes_SelfReferentialLinkedList() =>
        await AssertAgree(
            """
            public class Node
            {
                public byte V;
                public Node Next;
            }

            public static class Program
            {
                public static byte Main()
                {
                    Node a = new Node(); a.V = 1;
                    Node b = new Node(); b.V = 2;
                    Node c = new Node(); c.V = 3;
                    a.Next = b;
                    b.Next = c;
                    byte total = 0;
                    Node cur = a;
                    total = (byte)(total + cur.V);
                    cur = cur.Next;
                    total = (byte)(total + cur.V);
                    cur = cur.Next;
                    total = (byte)(total + cur.V);
                    return total; // 1+2+3
                }
            }
            """,
            A,
            (byte)6
        );

    // =============================================================================================
    // Arrays: fixed-size local array, sum with a widening accumulator (no overflow at element width).
    // =============================================================================================

    [Test]
    public async Task Arrays_FillAndSumWidensBeyondElementWidth() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static ushort Main()
                {
                    byte[] d = new byte[3] { 200, 200, 200 };
                    int sum = 0;
                    for (byte i = 0; i < 3; i++)
                        sum += d[i];
                    return (ushort)sum; // 600, not 600 % 256
                }
            }
            """,
            HL,
            (ushort)600
        );

    // =============================================================================================
    // Pointers: arithmetic walking an array, stackalloc round trip.
    // =============================================================================================

    // Pointer arithmetic walking a buffer (a `fixed (byte* p = managedArray)` block was tried first, but
    // Roslyn's fixed-statement codegen emits `ldlen` even with no explicit `.Length` in source — an
    // opcode outside the CIL frontend's phase-1 subset; see the class remarks on `.Length`/`ldlen`.
    // stackalloc sidesteps that entirely while still exercising `p++` pointer-increment arithmetic).
    [Test]
    public async Task Pointers_ArithmeticWalksAnArray() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static unsafe byte Main()
                {
                    byte* p = stackalloc byte[5];
                    p[0] = 1; p[1] = 2; p[2] = 3; p[3] = 4; p[4] = 5;
                    byte total = 0;
                    byte* q = p;
                    for (byte i = 0; i < 5; i++)
                    {
                        total = (byte)(total + *q);
                        q++;
                    }
                    return total; // 1+2+3+4+5
                }
            }
            """,
            A,
            (byte)15
        );

    [Test]
    public async Task Pointers_StackAllocBufferRoundTrips() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static unsafe byte Main()
                {
                    byte* buf = stackalloc byte[4];
                    for (byte i = 0; i < 4; i++)
                        buf[i] = (byte)(i * 3);
                    byte total = 0;
                    for (byte i = 0; i < 4; i++)
                        total = (byte)(total + buf[i]);
                    return total; // 0+3+6+9
                }
            }
            """,
            A,
            (byte)18
        );

    // =============================================================================================
    // Statics: mutable static field carried across calls, static readonly table.
    // =============================================================================================

    [Test]
    public async Task Statics_MutableCounterAcrossCalls() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static byte Counter;
                public static void Bump() { Counter = (byte)(Counter + 1); }

                public static byte Main()
                {
                    Bump(); Bump(); Bump(); Bump(); Bump();
                    return Counter;
                }
            }
            """,
            A,
            (byte)5
        );

    [Test]
    public async Task Statics_ReadonlyTableIndexedAtRuntime() =>
        await AssertAgree(
            """
            public static class Program
            {
                public static readonly byte[] Table = { 10, 20, 30, 40, 50 };

                public static byte Main()
                {
                    byte idx = 3;
                    return Table[idx];
                }
            }
            """,
            A,
            (byte)40
        );

    // =============================================================================================
    // Generics: a monomorphized generic method instantiated at two distinct types, transitively.
    // =============================================================================================

    [Test]
    public async Task Generics_TransitiveMonomorphization() =>
        await AssertAgree(
            """
            public static class Program
            {
                static T Identity<T>(T x) => x;
                static T Wrap<T>(T x) => Identity<T>(x);

                public static byte Main()
                {
                    byte b = Wrap<byte>(5);
                    ushort u = Wrap<ushort>(1000);
                    return (byte)(b + (u >> 3)); // 5 + 125
                }
            }
            """,
            A,
            (byte)130
        );

    // =============================================================================================
    // LINQ: Where/Select/Sum pipeline and Max directly on an array.
    // =============================================================================================

    [Test]
    public async Task Linq_WhereSelectSumPipeline() =>
        await AssertAgree(
            """
            using System.Linq;

            public static class Program
            {
                public static byte Main()
                {
                    int[] arr = new int[5] { 1, 2, 3, 4, 5 };
                    return (byte)arr.Where(x => x > 2).Select(x => x * 2).Sum(); // (3+4+5)*2 = 24
                }
            }
            """,
            A,
            (byte)24
        );

    [Test]
    public async Task Linq_MaxDirectlyOnArray() =>
        await AssertAgree(
            """
            using System.Linq;

            public static class Program
            {
                public static byte Main()
                {
                    int[] arr = new int[4] { 3, 9, 1, 7 };
                    return (byte)arr.Max();
                }
            }
            """,
            A,
            (byte)9
        );

    // =============================================================================================
    // Iterators: DELIBERATELY EXCLUDED from this A/B oracle. CSharpFrontend's coroutine support
    // (CLAUDE.md: "a linear run of yield returns... lowered to a MoveNext/Current state-machine
    // class") is not real C# on the consumer side: it requires the caller to declare a variable of
    // the frontend's own synthesized type name (`<Method>__Iter`, e.g. `Gen__Iter g = Gen();`, driven
    // by hand via `g.MoveNext()`/`g.Current()` — see CSharpEndToEndTests' Coroutine_* fixtures) and it
    // has no `ForEachStatementSyntax` handling and no support for `IEnumerable<T>` as an ordinary
    // local/return type at all (confirmed empirically: `foreach (byte b in Steps(5))` over a real
    // `IEnumerable<byte> Steps(...)` method is rejected with "unsupported type 'IEnumerable<byte>'").
    // `Gen__Iter` is not a type that exists anywhere in real, Roslyn-parseable C#, so no single source
    // text is simultaneously valid standard C# (for CilFrontend, via Roslyn) AND CSharpFrontend's own
    // iterator-consumption syntax. This is a genuine "cannot agree" case for SYNTACTIC reasons, not a
    // semantic divergence and not a CIL bug — there is no fixture in this category both frontends can
    // host, so none is included here. CilIteratorTests.cs already covers real foreach/IEnumerable<T>
    // correctness on the CilFrontend side (Debug vs. Release), which is the side that will remain.
    // =============================================================================================

    // =============================================================================================
    // Recursion: direct (Fibonacci) and mutual.
    // =============================================================================================

    [Test]
    public async Task Recursion_FibonacciDirect() =>
        await AssertAgree(
            """
            public static class Program
            {
                private static byte Fib(byte n)
                {
                    if (n < 2) return n;
                    return (byte)(Fib((byte)(n - 1)) + Fib((byte)(n - 2)));
                }

                public static byte Main() => Fib(10); // 55
            }
            """,
            A,
            (byte)55
        );

    [Test]
    public async Task Recursion_MutualIsEvenIsOdd() =>
        await AssertAgree(
            """
            public static class Program
            {
                private static bool IsEven(byte n) => n == 0 ? true : IsOdd((byte)(n - 1));
                private static bool IsOdd(byte n) => n == 0 ? false : IsEven((byte)(n - 1));

                public static byte Main() => (byte)(IsEven(10) ? 1 : 0);
            }
            """,
            A,
            (byte)1
        );

    // =============================================================================================
    // Softfloat: float and double arithmetic, routed through Koh.GameBoy.SoftFloat on both frontends.
    // =============================================================================================

    [Test]
    public async Task SoftFloat_SingleArithmetic()
    {
        float x = 1000000.0f;
        float y = 0.25f;
        float expected = x + y;
        await AssertAgree(
            $$"""
            using Koh.GameBoy;

            public static class Program
            {
                public static float Main()
                {
                    Hardware.Nop(); // force a Koh.GameBoy assembly reference so the CIL frontend's
                                     // [KohRuntime]-routine discovery can find SoftFloat in the emitted DLL
                    float a = {{x.ToString(
                "G9",
                System.Globalization.CultureInfo.InvariantCulture
            )}}f;
                    float b = {{y.ToString(
                "G9",
                System.Globalization.CultureInfo.InvariantCulture
            )}}f;
                    return a + b;
                }
            }
            """,
            I32,
            BitConverter.SingleToUInt32Bits(expected)
        );
    }

    [Test]
    public async Task SoftFloat_DoubleArithmetic()
    {
        double x = 1.0 / 3.0;
        double y = 2.5;
        double expected = x * y;
        await AssertAgree(
            $$"""
            using Koh.GameBoy;

            public static class Program
            {
                public static double Main()
                {
                    Hardware.Nop(); // force a Koh.GameBoy assembly reference (see SoftFloat_SingleArithmetic)
                    double a = {{x.ToString(
                "G17",
                System.Globalization.CultureInfo.InvariantCulture
            )}};
                    double b = {{y.ToString(
                "G17",
                System.Globalization.CultureInfo.InvariantCulture
            )}};
                    return a * b;
                }
            }
            """,
            I64,
            BitConverter.DoubleToUInt64Bits(expected)
        );
    }

    // =============================================================================================
    // DOCUMENTED DIVERGENCE: int promotion. Standard C#/CIL promotes byte operands to int for `*`
    // (ECMA-334 usual arithmetic conversions); Koh C# (CSharpFrontend) does not — the multiply happens
    // at byte width and wraps mod 256 BEFORE the result widens to int. This is the one place the two
    // frontends are intentionally allowed to disagree (see CLAUDE.md / project MEMORY.md:
    // "Koh C# has no int promotion"). This test does not assert equality between the frontends —
    // it documents and locks in the expected, DIFFERENT value each one legitimately produces.
    // =============================================================================================

    [Test]
    public async Task DocumentedDivergence_IntPromotion_ByteTimesLiteral_FrontendsIntentionallyDisagree()
    {
        const string src = """
            public static class Program
            {
                public static int Main()
                {
                    byte b = 20;
                    int r = b * 16;
                    return r;
                }
            }
            """;
        var cil = I32(RunCil(src));
        var cs = I32(RunCSharp(src));
        // Standard C#/CIL: `b * 16` promotes b to int first -> 20 * 16 = 320, no wrap.
        await Assert.That(cil).IsEqualTo(320u);
        // Koh C#: `b * 16` multiplies at byte width (b's width) -> 320 mod 256 = 64, then widens.
        await Assert.That(cs).IsEqualTo(64u);
        // The two frontends must genuinely disagree here -- if a future change made them agree, this
        // fixture would no longer be demonstrating the documented divergence it exists to pin down.
        await Assert.That(cil).IsNotEqualTo(cs);
    }
}
