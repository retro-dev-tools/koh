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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// Graphics library WAVE 3 / module B: Font + Text
/// (<c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c>, design §3 "Font.cs / Text.cs") —
/// <c>Koh.GameBoy.Graphics.Font</c> (built-in 96-glyph 1bpp ROM font) and <c>Text</c> (map-cell drawing
/// plus manual decimal <c>DrawNumber</c> formatting). Proves against a REAL compiled assembly ->
/// CilFrontend -> IrVerifier -> Sm83Backend -> Linker -> GameBoySystem pipeline that <c>Text.Draw(1, 0,
/// "SCORE")</c> — the exact "pleasant API" shape the design doc's resolved decision 3 calls for — writes
/// the expected $9800 map cells AND expands the glyph pixels into VRAM (<c>Font.LoadDefault</c> ->
/// <c>TileSet.Load1bpp</c>). Deliberately keeps its own compile-to-assembly harness rather than depending
/// on another test class's internals, mirroring <see cref="CilSpritesTests"/>'s own stated rationale for
/// doing the same.
/// </summary>
public class CilFontTextTests
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
        "koh-cil-fonttext-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilFontTextAsm_" + Guid.NewGuid().ToString("N"),
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
        var path = Path.Combine(ScratchDir, $"cil_fonttext_{Guid.NewGuid():N}.dll");
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
        IrOptimizer.Optimize(module);
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    // ---- Emulator harness (mirrors CilBgWinTests/CilSpritesTests) -------------------------------

    private static GameBoySystem Load(EmitModel model, out int start, HardwareMode mode)
    {
        var link = new LinkerType().Link([new Koh.Linker.Core.LinkerInput("cil", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        var gb = new GameBoySystem(mode, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    private static void Run(GameBoySystem gb, int start, int budget = 2_000_000)
    {
        for (int steps = 0; steps < budget; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                return;
            gb.StepInstruction();
        }
        throw new InvalidOperationException("program did not finish within the step budget");
    }

    // ---- Fixture 1: Text.Draw(1, 0, "SCORE") writes fontBase+(ch-0x20) map cells AND expands -------
    // ---- 'S's glyph pixels into VRAM (proves Font.LoadDefault -> TileSet.Load1bpp actually ran). ----
    private const string DrawScoreSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Font.LoadDefault(0);
                Text.Draw(1, 0, "SCORE");
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Draw_WritesExpectedMapCellsAndExpandsGlyphPixels(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DrawScoreSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(DrawScoreSource, level), out int start, HardwareMode.Dmg);
        Run(gb, start);

        // fontBase = 0 (Font.LoadDefault(0)); glyph = fontBase + ((byte)ch - 0x20).
        const string text = "SCORE";
        for (int i = 0; i < text.Length; i++)
        {
            byte expectedTile = (byte)(text[i] - 0x20);
            ushort mapAddr = (ushort)(0x9800 + (1 + i)); // row 0, col 1+i
            await Assert.That(gb.DebugReadByte(mapAddr)).IsEqualTo(expectedTile);
        }

        // 'S' (0x53) -> glyph index 0x33 = 51 -> VRAM tile base 0x8000 + 51*16 = 0x8330. Font.LoadDefault
        // expands with ink=3 (0b11), paper=0 (0b00), so low plane == high plane == the source 1bpp byte
        // (TileSet.ExpandPlane: inkMask=0xFF, paperMask=0x00 -> result = mono). Row 0 of 'S' is 0x3C,
        // row 1 is 0x40 (see Font.Glyphs's literal table).
        const ushort sTileBase = 0x8000 + 51 * 16;
        await Assert.That(gb.DebugReadByte(sTileBase)).IsEqualTo((byte)0x3C); // row 0 low
        await Assert.That(gb.DebugReadByte(sTileBase + 1)).IsEqualTo((byte)0x3C); // row 0 high
        await Assert.That(gb.DebugReadByte(sTileBase + 2)).IsEqualTo((byte)0x40); // row 1 low
        await Assert.That(gb.DebugReadByte(sTileBase + 3)).IsEqualTo((byte)0x40); // row 1 high
    }

    // ---- Fixture 2: DrawToWindow writes the SAME glyph indices to $9C00 instead of $9800 -----------
    private const string DrawToWindowSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Font.LoadDefault(0);
                Text.DrawToWindow(2, 3, "HI");
            }
        }
        """;

    [Test]
    public async Task DrawToWindow_WritesGlyphIndicesToWindowMap()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DrawToWindowSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(DrawToWindowSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        // Window map $9C00, row 3, col 2 and 3: 'H' (0x48 -> glyph 0x28), 'I' (0x49 -> glyph 0x29).
        ushort baseAddr = (ushort)(0x9C00 + 3 * 32 + 2);
        await Assert.That(gb.DebugReadByte(baseAddr)).IsEqualTo((byte)('H' - 0x20));
        await Assert.That(gb.DebugReadByte((ushort)(baseAddr + 1))).IsEqualTo((byte)('I' - 0x20));
        // $9800 (Bg map) at the same coordinates must be untouched (still 0 from Video.Init's clear).
        ushort bgAddr = (ushort)(0x9800 + 3 * 32 + 2);
        await Assert.That(gb.DebugReadByte(bgAddr)).IsEqualTo((byte)0);
    }

    // ---- Fixture 3: DrawNumber(0, 0, 1234, 5) right-aligned -> " 1234" (space-padded to width 5) -----
    private const string DrawNumberRightAlignedSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Font.LoadDefault(0);
                Text.DrawNumber(0, 0, 1234, 5);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task DrawNumber_RightAligned_SpacePadsToWidth(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DrawNumberRightAlignedSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(DrawNumberRightAlignedSource, level),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        // " 1234": col0 = space (glyph 0), col1..4 = '1','2','3','4' (glyph ch-0x20).
        await Assert.That(gb.DebugReadByte(0x9800)).IsEqualTo((byte)(' ' - 0x20));
        await Assert.That(gb.DebugReadByte(0x9801)).IsEqualTo((byte)('1' - 0x20));
        await Assert.That(gb.DebugReadByte(0x9802)).IsEqualTo((byte)('2' - 0x20));
        await Assert.That(gb.DebugReadByte(0x9803)).IsEqualTo((byte)('3' - 0x20));
        await Assert.That(gb.DebugReadByte(0x9804)).IsEqualTo((byte)('4' - 0x20));
    }

    // ---- Fixture 4: DrawNumber(col, row, value) left-aligned (no width) draws exactly digit-count ----
    // ---- tiles, no padding, including the value == 0 special case ("0", not an empty draw). ----------
    private const string DrawNumberLeftAlignedSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Font.LoadDefault(0);
                Text.DrawNumber(0, 0, 42);
                Text.DrawNumber(0, 1, 0);
            }
        }
        """;

    [Test]
    public async Task DrawNumber_LeftAligned_NoPadding_HandlesZero()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DrawNumberLeftAlignedSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(DrawNumberLeftAlignedSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        // Row 0: "42" at col 0-1; col 2 must be untouched (still 0 from Video.Init's clear) - proves no
        // trailing padding was drawn.
        await Assert.That(gb.DebugReadByte(0x9800)).IsEqualTo((byte)('4' - 0x20));
        await Assert.That(gb.DebugReadByte(0x9801)).IsEqualTo((byte)('2' - 0x20));
        await Assert.That(gb.DebugReadByte(0x9802)).IsEqualTo((byte)0);

        // Row 1: "0" at col 0 only.
        ushort row1 = 0x9800 + 32;
        await Assert.That(gb.DebugReadByte(row1)).IsEqualTo((byte)('0' - 0x20));
        await Assert.That(gb.DebugReadByte((ushort)(row1 + 1))).IsEqualTo((byte)0);
    }

    // ---- Fixture 5: the explicit-count byte[] fallback overload ------------------------------------
    private const string DrawByteArraySource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            static readonly byte[] Ascii = { (byte)'H', (byte)'I', (byte)'!' };

            public static void Main()
            {
                Video.Init();
                Font.LoadDefault(0);
                Text.Draw(4, 2, Ascii, 3);
            }
        }
        """;

    [Test]
    public async Task Draw_ByteArrayOverload_WritesExpectedGlyphIndices()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DrawByteArraySource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(DrawByteArraySource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        ushort baseAddr = (ushort)(0x9800 + 2 * 32 + 4);
        await Assert.That(gb.DebugReadByte(baseAddr)).IsEqualTo((byte)('H' - 0x20));
        await Assert.That(gb.DebugReadByte((ushort)(baseAddr + 1))).IsEqualTo((byte)('I' - 0x20));
        await Assert.That(gb.DebugReadByte((ushort)(baseAddr + 2))).IsEqualTo((byte)('!' - 0x20));
    }
}
