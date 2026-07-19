using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
using Koh.Compiler.Targets;
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
/// Task 3 of the CIL-frontend gap-closing phase (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>): the remaining data-shape gaps —
/// <c>System.Int128</c>/<c>UInt128</c>, and array literals outside the <c>static readonly</c>-field
/// idiom phase 4 already handled (<see cref="CilStaticsTests"/>'s <c>StaticReadonlyByteArray_*</c>
/// fixtures). Follows <see cref="CilEndToEndTests"/>'s own harness shape (its own compile-to-assembly
/// pipeline, per that file's remarks) rather than depending on it.
///
/// Roslyn emits <c>RuntimeHelpers.InitializeArray</c> for a LOCAL array literal
/// (<c>byte[] x = { 1, 2, 3 };</c>) via the exact same instruction shape it uses for a
/// <c>static readonly</c> field's initializer, and a <c>"..."u8</c> literal compiles to a
/// <c>ReadOnlySpan&lt;byte&gt;</c> constructed straight over RVA data. Both are the STANDARD C#
/// replacements for Koh's own former "string literal as byte[] initializer" idiom — that idiom was
/// Koh-legal-but-C#-illegal (Koh C# has no int-promotion, so it accepted syntax real C# rejects), so it
/// has no assembly representation at all and cannot be tested here; these two idioms are what a real
/// compiled assembly actually contains instead.
/// </summary>
public class CilLiteralTests
{
    // ---- Roslyn: compile real C# to a real assembly on disk (mirrors CilStaticsTests) ----------

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
        "koh-cil-literal-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilLiteralAsm_" + Guid.NewGuid().ToString("N"),
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
        var path = Path.Combine(ScratchDir, $"cil_literal_{Guid.NewGuid():N}.dll");
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

    private static EmitModel Compile(IrModule module)
    {
        IrOptimizer.Optimize(module); // CompilerDriver's default path (Mem2RegPass does SSA construction).
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    private static (IrModule Module, EmitModel Model) FrontendAndCompile(
        string source,
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(source, level, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        var model = Compile(module);
        return (module, model);
    }

    // ---- Emulator harness (mirrors CilStaticsTests) --------------------------------------------

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

    // ---- Fixture 1: a LOCAL byte[] array literal, indexed at runtime, must land in ROM ---------

    private const string LocalArraySource = """
        using Koh.GameBoy;

        public static class Program
        {
            private static byte ReadLocal(int i)
            {
                byte[] x = { 11, 22, 33, 44, 55 };
                return x[i];
            }

            public static void Main()
            {
                Hardware.LCDC = ReadLocal(1);
                Hardware.BGP = ReadLocal(4);
            }
        }
        """;

    // E4 (length-carrying arrays): the counted ROM global is [u16 element count][payload].
    private static readonly byte[] ExpectedLocalArrayBytes = [5, 0, 11, 22, 33, 44, 55];

    private static (byte Lcdc, byte Bgp) RunLocalArray(OptimizationLevel level)
    {
        var (_, model) = FrontendAndCompile(LocalArraySource, level);
        var gb = Load(model, out int s, out int l);
        Run(gb, s, l);
        return (gb.DebugReadByte(0xFF40), gb.DebugReadByte(0xFF47));
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task LocalArrayLiteral_LandsInRomWithInitialBytesNotRebuiltAtStartup(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(LocalArraySource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // Direct proof it "landed in ROM" (not rebuilt from a runtime heap allocation + a per-element
        // store loop at every call, the way an ordinary `newarr` would): the module carries a ROM
        // global whose OWN initializer already holds the literal's bytes (count-prefixed since E4),
        // straight from Cecil's RVA blob — no separate heap allocation for this array at all.
        var romGlobal = module.Globals.Single(g => g.Name.Contains("__arr."));
        await Assert.That(romGlobal.AddressSpace).IsEqualTo(AddressSpace.Rom);
        await Assert.That(romGlobal.Initializer).IsNotNull();
        await Assert.That(romGlobal.Initializer!).IsEquivalentTo(ExpectedLocalArrayBytes);

        var (lcdc, bgp) = RunLocalArray(level);
        await Assert.That(lcdc).IsEqualTo((byte)22); // x[1]
        await Assert.That(bgp).IsEqualTo((byte)55); // x[4]
    }

    [Test]
    public async Task LocalArrayLiteral_DebugAndReleaseProduceIdenticalObservableState()
    {
        await Assert
            .That(RunLocalArray(OptimizationLevel.Release))
            .IsEqualTo(RunLocalArray(OptimizationLevel.Debug));
    }

    // ---- Fixture 2: a `"..."u8` ReadOnlySpan<byte> literal, indexed at runtime, must land in ROM -

    private const string U8LiteralSource = """
        using System;
        using Koh.GameBoy;

        public static class Program
        {
            private static byte ReadU8(int i)
            {
                ReadOnlySpan<byte> s = "Koh!"u8;
                return s[i];
            }

            public static void Main()
            {
                Hardware.LCDC = ReadU8(0);
                Hardware.BGP = ReadU8(3);
            }
        }
        """;

    private static readonly byte[] ExpectedU8Bytes = "Koh!"u8.ToArray();

    // Roslyn's own RVA blob for a u8 literal is ONE BYTE LONGER than the literal itself — confirmed
    // against a real Cecil dump (`__StaticArrayInitTypeSize=5` for a 4-byte literal): it appends a
    // trailing NUL the ReadOnlySpan<byte>.ctor's own length argument (the real, correct span length)
    // never counts. So the ROM global's raw Initializer legitimately carries one extra byte beyond
    // what the program ever reads — the frontend must NOT truncate it (that would corrupt whichever
    // OTHER u8 literal in the program happens to be byte-identical up to this one's own length, since
    // CilLoweringContext.EnsureRvaBlobGlobal caches by blob FIELD, matching Roslyn's own content-based
    // deduplication).
    private static readonly byte[] ExpectedU8BlobBytes = [.. ExpectedU8Bytes, 0];

    private static (byte Lcdc, byte Bgp) RunU8Literal(OptimizationLevel level)
    {
        var (_, model) = FrontendAndCompile(U8LiteralSource, level);
        var gb = Load(model, out int s, out int l);
        Run(gb, s, l);
        return (gb.DebugReadByte(0xFF40), gb.DebugReadByte(0xFF47));
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task U8Literal_LandsInRomWithInitialBytesNotRebuiltAtStartup(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(U8LiteralSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var romGlobal = module.Globals.Single(g => g.Name.Contains("__rvablob"));
        await Assert.That(romGlobal.AddressSpace).IsEqualTo(AddressSpace.Rom);
        await Assert.That(romGlobal.Initializer).IsNotNull();
        await Assert.That(romGlobal.Initializer!).IsEquivalentTo(ExpectedU8BlobBytes);

        var (lcdc, bgp) = RunU8Literal(level);
        await Assert.That(lcdc).IsEqualTo(ExpectedU8Bytes[0]); // 'K'
        await Assert.That(bgp).IsEqualTo(ExpectedU8Bytes[3]); // '!'
    }

    [Test]
    public async Task U8Literal_DebugAndReleaseProduceIdenticalObservableState()
    {
        await Assert
            .That(RunU8Literal(OptimizationLevel.Release))
            .IsEqualTo(RunU8Literal(OptimizationLevel.Debug));
    }

    // ---- Fixture 3: Int128/UInt128 arithmetic round trip ---------------------------------------

    // Every IL opcode ECMA-335 has for arithmetic/comparison/shift/conversion is exercised here, but
    // via a CALL to the type's own operator method (op_Addition, op_LessThan, op_Implicit, ...) — the
    // ONLY shape Roslyn ever emits for Int128/UInt128 (confirmed against a real Cecil dump; see
    // CilMethodLowerer's TryLowerInt128Operator), never a primitive opcode. Every intermediate value
    // (add/sub/mul/div/shift/compare) is computed by real System.Int128/UInt128 arithmetic at test-
    // authoring time below, not hand-calculated, so a wrong expected value here would be a bug in the
    // TEST rather than a plausible typo this review could miss.
    private const string Int128Source = """
        using Koh.GameBoy;

        public static class Program
        {
            public static void Main()
            {
                System.Int128 a = 123456789012345678;
                System.Int128 b = 987654321098765432;
                System.Int128 sum = a + b;
                System.Int128 diff = sum - b; // == a
                System.Int128 prod = a * 1000;
                System.Int128 quot = prod / 1000; // == a
                System.UInt128 ua = 250;
                System.UInt128 ushifted = ua << 4; // 4000

                bool eqDiff = diff == a;
                bool eqQuot = quot == a;
                bool lt = a < b;
                int flags = (eqDiff ? 1 : 0) + (eqQuot ? 2 : 0) + (lt ? 4 : 0);

                Hardware.LCDC = (byte)sum;
                Hardware.BGP = (byte)(sum >> 40);
                Hardware.SCX = (byte)ushifted;
                Hardware.SCY = (byte)(ushifted >> 8);
                Hardware.WX = (byte)flags;
            }
        }
        """;

    private static (byte Lcdc, byte Bgp, byte Scx, byte Scy, byte Wx) RunInt128(
        OptimizationLevel level
    )
    {
        var (_, model) = FrontendAndCompile(Int128Source, level);
        var gb = Load(model, out int s, out int l);
        Run(gb, s, l);
        return (
            gb.DebugReadByte(0xFF40),
            gb.DebugReadByte(0xFF47),
            gb.DebugReadByte(0xFF43),
            gb.DebugReadByte(0xFF42),
            gb.DebugReadByte(0xFF4B)
        );
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Int128Arithmetic_RoundTripsThroughRealArithmeticOnTheHost(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(Int128Source, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // The host's own real System.Int128/UInt128 arithmetic is the oracle — every value below is
        // COMPUTED, not hand-transcribed.
        System.Int128 a = 123456789012345678;
        System.Int128 b = 987654321098765432;
        System.Int128 sum = a + b;
        System.Int128 diff = sum - b;
        System.Int128 prod = a * 1000;
        System.Int128 quot = prod / 1000;
        System.UInt128 ua = 250;
        System.UInt128 ushifted = ua << 4;
        var eqDiff = diff == a;
        var eqQuot = quot == a;
        var lt = a < b;
        var expectedFlags = (byte)((eqDiff ? 1 : 0) + (eqQuot ? 2 : 0) + (lt ? 4 : 0));

        var expectedLcdc = (byte)sum;
        var expectedBgp = (byte)(sum >> 40);
        var expectedScx = (byte)ushifted;
        var expectedScy = (byte)(ushifted >> 8);

        var (lcdc, bgp, scx, scy, wx) = RunInt128(level);
        await Assert.That(lcdc).IsEqualTo(expectedLcdc);
        await Assert.That(bgp).IsEqualTo(expectedBgp);
        await Assert.That(scx).IsEqualTo(expectedScx);
        await Assert.That(scy).IsEqualTo(expectedScy);
        await Assert.That(wx).IsEqualTo(expectedFlags);
        // Sanity: the round trip (diff == a, quot == a) and the comparison (a < b) really did hold on
        // the host too, or this fixture would be vacuously asserting nothing.
        await Assert.That(expectedFlags).IsEqualTo((byte)7);
    }

    [Test]
    public async Task Int128Arithmetic_DebugAndReleaseProduceIdenticalObservableState()
    {
        await Assert
            .That(RunInt128(OptimizationLevel.Release))
            .IsEqualTo(RunInt128(OptimizationLevel.Debug));
    }
}
