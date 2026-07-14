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
/// The value-type/memory data-model task (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>'s struct/pointer task): value-type
/// structs (locals, fields, whole-struct copy), raw pointers (<c>ldind</c>/<c>stind</c>, pointer
/// arithmetic, <c>localloc</c>), byref (<c>ref</c>) parameters, and the CIL <c>switch</c> jump table.
/// Same harness shape as <see cref="CilEndToEndTests"/> (own compile-to-assembly copy, per that file's
/// own convention) — every fixture below is compiled by Roslyn to a REAL assembly, in both Debug and
/// Release IL, lowered by <see cref="CilFrontend"/>, verified clean, run through the SM83 backend,
/// linked to a ROM, and executed on <see cref="GameBoySystem"/>; Debug and Release are asserted to
/// produce byte-identical observable state.
/// </summary>
public class CilStructTests
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
        "koh-cil-struct-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilStructAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true // every fixture below pokes a pointer or stackalloc's a buffer
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_struct_{Guid.NewGuid():N}.dll");
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

    // ---- Emulator harness (mirrors CSharpEndToEndTests / CilEndToEndTests) --------------------

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

    /// <summary>Full contract for one fixture: verifies clean under both configs, produces the
    /// expected computed value under both, and Debug/Release agree with each other.</summary>
    private static async Task AssertFixture(string source, byte expected)
    {
        foreach (var level in new[] { OptimizationLevel.Debug, OptimizationLevel.Release })
        {
            var diagnostics = new DiagnosticBag();
            var module = Frontend(source, level, diagnostics);
            await Assert
                .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
                .IsFalse();
            await Assert.That(IrVerifier.Verify(module)).IsEmpty();
        }
        var debugValue = RunAndReadBgp(source, OptimizationLevel.Debug);
        var releaseValue = RunAndReadBgp(source, OptimizationLevel.Release);
        await Assert.That(debugValue).IsEqualTo(expected);
        await Assert.That(releaseValue).IsEqualTo(expected);
    }

    // ---- 1. Struct field round-trip through a ROM, with a whole-struct copy aliasing check ----
    //
    // a.X=10, a.Y=20; b = a (whole-struct copy); b.X += 5. If the copy aliased 'a' instead of
    // duplicating its bytes, a.X would also read back as 15 and the sum would be 70, not 65 — this
    // is exactly the failure mode a broken/aliased struct assignment would produce.
    private const string StructFieldRoundTripSource = """
        using Koh.GameBoy;

        public struct Point
        {
            public byte X;
            public byte Y;
        }

        public class Program
        {
            public static void Main()
            {
                Point a;
                a.X = 10;
                a.Y = 20;
                Point b = a;
                b.X = (byte)(b.X + 5);
                byte sum = (byte)(a.X + a.Y + b.X + b.Y);
                Hardware.BGP = sum;
            }
        }
        """;

    [Test]
    public async Task StructFieldRoundTrip_WholeCopyDoesNotAlias() =>
        await AssertFixture(StructFieldRoundTripSource, expected: 65);

    // ---- Nested structs + arrays of structs (extra coverage for item 1) ------------------------

    private const string NestedAndArrayStructSource = """
        using Koh.GameBoy;

        public struct Point { public byte X; public byte Y; }
        public struct Line { public Point Start; public Point End; }

        public class Program
        {
            public static void Main()
            {
                Line line;
                line.Start.X = 1;
                line.Start.Y = 2;
                line.End.X = 3;
                line.End.Y = 4;

                Point[] pts = new Point[3];
                pts[0].X = 5;
                pts[1].X = 6;
                pts[2].X = 7;

                byte sum = (byte)(
                    line.Start.X + line.Start.Y + line.End.X + line.End.Y
                    + pts[0].X + pts[1].X + pts[2].X
                );
                Hardware.BGP = sum;
            }
        }
        """;

    [Test]
    public async Task NestedStructsAndArraysOfStructs_FieldsRoundTrip() =>
        await AssertFixture(NestedAndArrayStructSource, expected: 28);

    // ---- 2. A pointer write to VRAM, read back ---------------------------------------------------

    // Hardware.LCDC = 0 first: VRAM is PPU-mode-gated on real hardware (CLAUDE.md's Mem.Copy remark:
    // "NOT vblank-aware — caller's responsibility"), so a raw VRAM poke with the LCD left on is
    // legitimately flaky (the emulator models the same access restriction real hardware has) —
    // disabling the LCD first is the realistic, idiomatic way real Koh code guarantees VRAM access.
    private const string PointerVramRoundTripSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static unsafe void Main()
            {
                Hardware.LCDC = 0;
                byte* vram = Gb.Vram;
                *vram = 0x2A;
                byte readBack = *vram;
                Hardware.BGP = readBack;
            }
        }
        """;

    [Test]
    public async Task PointerWriteToVram_ReadBack() =>
        await AssertFixture(PointerVramRoundTripSource, expected: 0x2A);

    // Pointer arithmetic: write through vram + 3 (stind/ldind after an AddOrSub-lowered gep), so the
    // frontend's byte-scaled-offset gep path (see CilMethodLowerer.AddOrSub) is exercised for real,
    // not just a bare dereference of the intrinsic pointer itself.
    private const string PointerArithmeticSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static unsafe void Main()
            {
                Hardware.LCDC = 0;
                byte* vram = Gb.Vram;
                byte* p = vram + 3;
                *p = 0x11;
                *(p + 1) = 0x22;
                byte sum = (byte)(vram[3] + vram[4]);
                Hardware.BGP = sum;
            }
        }
        """;

    [Test]
    public async Task PointerArithmetic_OffsetWriteReadBack() =>
        await AssertFixture(PointerArithmeticSource, expected: 0x11 + 0x22);

    // ---- 3. A stackalloc buffer -------------------------------------------------------------------

    private const string StackAllocSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static unsafe void Main()
            {
                byte* buf = stackalloc byte[4];
                buf[0] = 1;
                buf[1] = 2;
                buf[2] = 3;
                buf[3] = 4;
                byte sum = (byte)(buf[0] + buf[1] + buf[2] + buf[3]);
                Hardware.BGP = sum;
            }
        }
        """;

    [Test]
    public async Task StackAllocBuffer_RoundTrips() =>
        await AssertFixture(StackAllocSource, expected: 10);

    // ---- 4. A ref parameter mutated by a callee -----------------------------------------------

    private const string RefParameterSource = """
        using Koh.GameBoy;

        public class Program
        {
            static void Increment(ref byte x)
            {
                x = (byte)(x + 1);
            }

            public static void Main()
            {
                byte v = 41;
                Increment(ref v);
                Hardware.BGP = v;
            }
        }
        """;

    [Test]
    public async Task RefParameter_MutatedByCallee() =>
        await AssertFixture(RefParameterSource, expected: 42);

    // A ref STRUCT parameter: the callee mutates the caller's own storage directly (no copy) —
    // distinguishes real ref semantics from PrepareArg's byval-copy path (CilMethodLowerer.Structs.cs).
    private const string RefStructParameterSource = """
        using Koh.GameBoy;

        public struct Point { public byte X; public byte Y; }

        public class Program
        {
            static void Move(ref Point p, byte dx)
            {
                p.X = (byte)(p.X + dx);
            }

            public static void Main()
            {
                Point a;
                a.X = 10;
                a.Y = 20;
                Move(ref a, 7);
                Hardware.BGP = (byte)(a.X + a.Y);
            }
        }
        """;

    [Test]
    public async Task RefStructParameter_MutatesCallersStorage() =>
        await AssertFixture(RefStructParameterSource, expected: 37);

    // A BYVAL struct parameter: the callee's mutation must NOT be visible to the caller (a real,
    // independent copy — see CilMethodLowerer.Structs.cs's PrepareArg).
    private const string ByValStructParameterSource = """
        using Koh.GameBoy;

        public struct Point { public byte X; public byte Y; }

        public class Program
        {
            static void TryMove(Point p, byte dx)
            {
                p.X = (byte)(p.X + dx);
            }

            public static void Main()
            {
                Point a;
                a.X = 10;
                a.Y = 20;
                TryMove(a, 7);
                Hardware.BGP = (byte)(a.X + a.Y);
            }
        }
        """;

    [Test]
    public async Task ByValStructParameter_CalleeMutationNotVisibleToCaller() =>
        await AssertFixture(ByValStructParameterSource, expected: 30);

    // ---- 5. A switch dispatch (the CIL 'switch' jump-table opcode) -----------------------------
    //
    // Ten contiguous, densely-packed cases (0..9) reliably makes Roslyn emit a real jump table (the
    // 'switch' opcode) rather than an if/else compare chain, in both Debug and Release.
    private const string SwitchDispatchSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
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
                Hardware.BGP = result;
            }
        }
        """;

    [Test]
    public async Task SwitchDispatch_JumpTable_SelectsCorrectCase() =>
        await AssertFixture(SwitchDispatchSource, expected: 66);

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task SwitchDispatch_LowersToRealSwitchInstruction(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(SwitchDispatchSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        var hasSwitch = module.Functions.Any(f =>
            f.Blocks.Any(b => b.Instructions.Any(i => i is SwitchInstruction))
        );
        await Assert.That(hasSwitch).IsTrue();
    }

    // ---- Default case of the switch, to make sure the fallthrough/default path is real too -----

    private const string SwitchDefaultSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                int selector = 42;
                byte result;
                switch (selector)
                {
                    case 0: result = 10; break;
                    case 1: result = 11; break;
                    case 2: result = 12; break;
                    case 3: result = 13; break;
                    case 4: result = 14; break;
                    case 5: result = 15; break;
                    case 6: result = 16; break;
                    case 7: result = 17; break;
                    case 8: result = 18; break;
                    case 9: result = 19; break;
                    default: result = 77; break;
                }
                Hardware.BGP = result;
            }
        }
        """;

    [Test]
    public async Task SwitchDispatch_OutOfRange_FallsThroughToDefault() =>
        await AssertFixture(SwitchDefaultSource, expected: 77);

    // ---- Enums (just their underlying integer in IL) --------------------------------------------

    private const string EnumSource = """
        using Koh.GameBoy;

        public enum Level : byte
        {
            Low = 1,
            Medium = 2,
            High = 3,
        }

        public class Program
        {
            public static void Main()
            {
                Level lvl = Level.Medium;
                byte result = lvl == Level.Medium ? (byte)200 : (byte)0;
                Hardware.BGP = result;
            }
        }
        """;

    [Test]
    public async Task Enum_UnderlyingIntegerRoundTrips() =>
        await AssertFixture(EnumSource, expected: 200);
}
