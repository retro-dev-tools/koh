using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Ir;
using Koh.Core.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Samples;

/// <summary>
/// Compiles the real <c>samples/gb-gfx-demo</c> showcase — one ROM exercising every module under
/// <c>Koh.GameBoy.Graphics</c> (graphics-library design doc §5, item 3) — through the real pipeline
/// (Roslyn -&gt; <see cref="CilFrontend"/> -&gt; IR -&gt; <see cref="Sm83Backend"/> -&gt; linker -&gt;
/// <see cref="GameBoySystem"/>), mirroring <c>Cube3dTests</c>'s technique for a real, unmodified,
/// infinitely-looping demo: <c>gb.RunFrame()</c> a bounded number of times, then inspect hardware state
/// directly (OAM, the window tilemap, LCDC, palette registers/RAM) — no test-only entry point, no source
/// modification.
///
/// <b>Why assertions are driven off the ROM's own SCORE readout instead of a hand-picked frame count:</b>
/// the demo's game loop is <c>while (true) { ...; Video.EndFrame(); }</c> with no return, so (unlike
/// <c>CilGame2048Tests</c>'s bounded synthetic entry points) there is no way to stop exactly "after 1
/// EndFrame" by watching the program counter leave a code range. Instead: the demo draws its own
/// completed-iteration count to the window map every iteration (<c>score += 17; DrawNumberToWindow(...)</c>
/// before <c>Video.EndFrame()</c>) — decoding those digits back to an integer (<see cref="DecodeScore"/>)
/// gives the EXACT number of completed iterations F as of whatever point <c>RunFrame()</c> happened to
/// stop at, with no dependency on cycle-per-instruction assumptions. Every other assertion (sprite OAM
/// position, active palette theme) is then a closed-form function of that same F, mirrored from the
/// sample's own Game.cs constants/formulas (documented per assertion below) — so this test is exact, not
/// a range/tolerance check, while remaining robust to however many loop iterations a given
/// <c>RunFrame()</c> budget happens to complete.
/// </summary>
public class GbGfxDemoTests
{
    // ---- Real sample source, read once (mirrors Cube3dTests.ReadDemo) --------------------------

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static readonly string DemoSource = File.ReadAllText(
        Path.Combine(Root(), "samples", "gb-gfx-demo", "Game.cs")
    );

    // ---- Roslyn: compile the real sample to a real assembly, referencing Koh.GameBoy.dll ---------

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
        "koh-gb-gfx-demo-tests"
    );

    // The Koh SDK brings Koh.GameBoy into scope everywhere via a global <Using> (see Sdk.props), so the
    // sample file needs no `using` of its own for it; compiling it directly with Roslyn (no SDK) needs
    // the same global using injected explicitly.
    private const string GlobalUsings = "global using Koh.GameBoy;\n";

    private static string CompileToAssembly(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(GlobalUsings + source);
        var compilation = CSharpCompilation.Create(
            "GbGfxDemoAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                // Release IL: RunFrame budgets below are sized against real (optimized) render cadence,
                // matching Cube3dTests's own rationale for the same choice.
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"gfxdemo_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    private static CompilerInput InputFor(string source)
    {
        var assemblyPath = CompileToAssembly(source);
        return CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
    }

    /// <summary>Frontend -&gt; IR only (no optimizer, no backend) — used by the diagnostics/verifier
    /// checks below, mirroring every other CIL test file's own <c>Frontend</c> helper.</summary>
    private static IrModule Frontend(string source, DiagnosticBag diagnostics) =>
        new CilFrontend().Lower(InputFor(source), diagnostics);

    /// <summary>Mirrors <c>Koh.Build.Tasks.CompileKohRom</c> exactly (<c>CompilerDriver.Compile</c>,
    /// which optimizes by default) — the real ROM build runs the IR optimizer. Produces the linked ROM,
    /// same as <c>Cube3dTests.Compile</c>.</summary>
    private static byte[] Compile(string source)
    {
        var diagnostics = new DiagnosticBag();
        var model = CompilerDriver.Compile(
            new CilFrontend(),
            new Sm83Backend(),
            InputFor(source),
            diagnostics
        );
        if (diagnostics.Any(d => d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Error))
            throw new InvalidOperationException(
                string.Join("; ", diagnostics.Select(d => d.Message))
            );
        var link = new LinkerType().Link([new Koh.Linker.Core.LinkerInput("gfxdemo", model)]);
        if (!link.Success || link.RomData is null)
            throw new InvalidOperationException(
                string.Join("; ", link.Diagnostics.Select(d => d.Message))
            );
        return link.RomData;
    }

    private static GameBoySystem Boot(byte[] rom, HardwareMode mode)
    {
        var gb = new GameBoySystem(mode, CartridgeFactory.Load(rom));
        gb.Registers.Pc = 0x100; // boot: NOP; JP entry
        gb.Registers.Sp = 0xFFFE;
        return gb;
    }

    // ---- Mirrors of the sample's own layout/formulas (Game.cs), used to compute expected values ----

    private const byte FontFirstTile = 16;
    private const byte SpaceGlyphTile = FontFirstTile; // ' ' - 0x20 == 0 -> FirstTile + 0
    private const byte DigitGlyphBase = FontFirstTile + 0x10; // '0' - 0x20 == 0x10 -> FirstTile + 0x10

    private const byte OrbSpriteTile = 2;
    private const ushort WindowMapBase = 0x9C00;
    private const ushort OamBase = 0xFE00;

    private static readonly sbyte[] OrbitDx =
    {
        24,
        22,
        17,
        9,
        0,
        -9,
        -17,
        -22,
        -24,
        -22,
        -17,
        -9,
        0,
        9,
        17,
        22,
    };
    private static readonly sbyte[] OrbitDy =
    {
        0,
        9,
        17,
        22,
        24,
        22,
        17,
        9,
        0,
        -9,
        -17,
        -22,
        -24,
        -22,
        -17,
        -9,
    };
    private const int OrbitCenterX = 80;
    private const int OrbitCenterY = 72;
    private const int OrbitSteps = 16;
    private const int PaletteChangeFrames = 60;
    private static readonly byte[] OrbitAttrs = { 0x00, 0x20, 0x40, 0x60 }; // none, FlipX, FlipY, both

    /// <summary>Reads the window map's "SCORE " + 5-digit number HUD back and returns the decoded score
    /// value, or -1 if the number field is not a fully self-consistent reading. Two distinct cases both
    /// legitimately return -1: (a) no loop iteration has drawn it yet (columns still show the
    /// checkerboard's own tile 0, not a glyph at all), and (b) a torn read — <see cref="WaitForStableScore"/>
    /// samples this in the middle of arbitrary instruction execution (not just frame boundaries), which
    /// could in principle land between two of <c>Text.DrawNumberToWindow</c>'s five per-column
    /// <c>Win.SetTile</c> writes, mixing glyphs from two different score values. Every individual column
    /// write is still atomic (one instruction, one byte), so a torn read never produces a byte outside the
    /// valid glyph set (space or digit) — it just produces a numeric value that is not a multiple of 17
    /// (this HUD's fixed per-iteration increment). <see cref="WaitForStableScore"/> filters exactly that
    /// with its own <c>score % 17 == 0</c> check, so this method only needs to guarantee "always a real
    /// glyph or -1", not full cross-column consistency by itself.</summary>
    private static int DecodeScore(GameBoySystem gb)
    {
        int value = 0;
        bool any = false;
        for (int col = 6; col <= 10; col++)
        {
            byte tile = gb.DebugReadByte((ushort)(WindowMapBase + col));
            if (tile == SpaceGlyphTile)
                continue;
            if (tile < DigitGlyphBase || tile > DigitGlyphBase + 9)
                return -1; // not a glyph at all yet - loop hasn't drawn the HUD
            any = true;
            value = value * 10 + (tile - DigitGlyphBase);
        }
        return any ? value : -1;
    }

    /// <summary>Steps <paramref name="gb"/> one CPU instruction at a time (not <c>RunFrame()</c>'s fixed
    /// cycle budget, which can stop mid-write — see <see cref="DecodeScore"/>'s remarks) until the window
    /// HUD reads back a self-consistent score (a positive multiple of 17), skipping
    /// <paramref name="minSteps"/> instructions unconditionally first (used to force visiting a LATER
    /// iteration on a second call against the same, already-running <paramref name="gb"/> — otherwise a
    /// second call would just re-observe the state the first call already stabilized on). Returns the
    /// completed-iteration count F such that <c>score == 17 * F</c> (score is drawn AFTER incrementing —
    /// see Game.cs's <c>score += 17; Text.DrawNumberToWindow(...)</c> ordering).</summary>
    /// <summary>Requires F &gt;= 2 (score &gt;= 34), not just F &gt;= 1: OAM only reflects a fully-settled
    /// flush through iteration <c>F - 2</c> at the instant iteration <c>F - 1</c>'s score becomes visible
    /// — see <see cref="ExpectedSprite"/>'s remarks for why. F = 1 would mean no completed EndFrame has
    /// ever flushed the shadow to real OAM yet (Video.Init's clear is still the only thing OAM has ever
    /// seen), so every caller needs the same F &gt;= 2 floor regardless of whether it reads OAM.</summary>
    private static int WaitForStableScore(GameBoySystem gb, int minSteps, int maxSteps)
    {
        int steps = 0;
        for (; steps < minSteps; steps++)
            gb.StepInstruction();
        for (; steps < maxSteps; steps++)
        {
            gb.StepInstruction();
            int score = DecodeScore(gb);
            if (score >= 34 && score % 17 == 0)
                return score / 17;
        }
        throw new InvalidOperationException(
            $"HUD score never stabilized within {maxSteps} instruction steps ({minSteps} skipped first)"
        );
    }

    /// <summary>Expected OAM bytes (Y, X, Tile, Attr) for sprite <paramref name="slot"/> (0-3), sampled
    /// at the exact instant the window HUD's score first reads <c>17 * completedIterations</c> (what
    /// <see cref="WaitForStableScore"/> returns as F). Game.cs draws that score BEFORE calling
    /// <c>Video.EndFrame()</c> in the SAME iteration (F-1, 0-based), so at that sampling instant
    /// iteration F-1's own OAM flush has NOT happened yet — the most recently completed flush is from
    /// iteration F-2 (iteration F-2's entire body, including its own <c>EndFrame</c>, is a strict
    /// prerequisite of the program even reaching iteration F-1's body, let alone its score draw). Hence
    /// <c>F - 2</c>, not <c>F - 1</c>, here — verified empirically against the harness before this
    /// comment was written (see this class's own passing <c>Oam_AfterFirstPaintedIteration...</c> test).
    /// Mirrors Game.cs's orbit-index/attr assignment exactly.</summary>
    private static (byte y, byte x, byte tile, byte attr) ExpectedSprite(
        int completedIterations,
        int slot
    )
    {
        int lastFlushedIteration = completedIterations - 2;
        int orbitStepAtLastMove = ((lastFlushedIteration % OrbitSteps) + OrbitSteps) % OrbitSteps;
        int index = (orbitStepAtLastMove + slot * (OrbitSteps / 4)) % OrbitSteps;
        int x = OrbitCenterX + OrbitDx[index];
        int y = OrbitCenterY + OrbitDy[index];
        return ((byte)(y + 16), (byte)(x + 8), OrbSpriteTile, OrbitAttrs[slot]);
    }

    /// <summary>Which of the two <c>ApplyPalette</c> themes (false = A, true = B) is active once F
    /// iterations have completed — mirrors Game.cs's <c>Video.FrameCount % PaletteChangeFrames == 0</c>
    /// toggle: iteration k (0-based) applies a theme switch exactly when <c>k % 60 == 0</c>, so the
    /// number of switches applied after F completed iterations is <c>((F-1) / 60) + 1</c>, and the active
    /// theme is that count's parity.</summary>
    private static bool ActiveThemeIsAlt(int completedIterations)
    {
        if (completedIterations <= 0)
            return false;
        int switches = (completedIterations - 1) / PaletteChangeFrames + 1;
        return switches % 2 == 0;
    }

    // ---- The real sample compiles without diagnostics and links to a bootable ROM -----------------

    [Test]
    public async Task Demo_CompilesWithoutDiagnostics()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DemoSource, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Demo_LinksToBootableRom()
    {
        var rom = Compile(DemoSource);

        await Assert.That(rom[0x100]).IsEqualTo((byte)0x00); // NOP
        await Assert.That(rom[0x101]).IsEqualTo((byte)0xC3); // JP a16
        await Assert.That(rom[0x104]).IsEqualTo((byte)0xCE); // first byte of the Nintendo logo
        await Assert.That(rom[0x105]).IsEqualTo((byte)0xED);
    }

    // ---- Window HUD ---------------------------------------------------------------------------

    [Test]
    public async Task WindowMap_SpellsScoreViaFontBasePlusOffset()
    {
        var rom = Compile(DemoSource);
        var gb = Boot(rom, HardwareMode.Dmg);
        var completed = WaitForStableScore(gb, minSteps: 0, maxSteps: 2_000_000);
        await Assert.That(completed).IsGreaterThanOrEqualTo(2);

        // "SCORE " at window columns 0..5 -> FontFirstTile + (ch - 0x20).
        byte[] expected =
        {
            (byte)(FontFirstTile + ('S' - 0x20)),
            (byte)(FontFirstTile + ('C' - 0x20)),
            (byte)(FontFirstTile + ('O' - 0x20)),
            (byte)(FontFirstTile + ('R' - 0x20)),
            (byte)(FontFirstTile + ('E' - 0x20)),
            (byte)(FontFirstTile + (' ' - 0x20)),
        };
        for (int col = 0; col < expected.Length; col++)
            await Assert
                .That(gb.DebugReadByte((ushort)(WindowMapBase + col)))
                .IsEqualTo(expected[col])
                .Because($"window column {col}");
    }

    // ---- LCDC layer bits ------------------------------------------------------------------------

    [Test]
    public async Task Lcdc_HasBgObjAndWindowEnabled()
    {
        var rom = Compile(DemoSource);
        var gb = Boot(rom, HardwareMode.Dmg);
        WaitForStableScore(gb, minSteps: 0, maxSteps: 2_000_000);

        byte lcdc = gb.DebugReadByte(0xFF40);
        await Assert.That((lcdc & 0x80) != 0).IsTrue().Because("LCD on");
        await Assert.That((lcdc & 0x01) != 0).IsTrue().Because("BG enable");
        await Assert.That((lcdc & 0x02) != 0).IsTrue().Because("OBJ enable");
        await Assert.That((lcdc & 0x20) != 0).IsTrue().Because("window enable");
    }

    // ---- Sprite OAM: exact Y/X/tile/attr after the ROM's own first painted iteration --------------

    [Test]
    public async Task Oam_AfterFirstPaintedIteration_MatchesExpectedYXTileAttr()
    {
        var rom = Compile(DemoSource);
        var gb = Boot(rom, HardwareMode.Dmg);
        int completed = WaitForStableScore(gb, minSteps: 0, maxSteps: 2_000_000);

        for (int slot = 0; slot < 4; slot++)
        {
            var (y, x, tile, attr) = ExpectedSprite(completed, slot);
            ushort baseAddr = (ushort)(OamBase + slot * 4);
            await Assert.That(gb.DebugReadByte(baseAddr)).IsEqualTo(y).Because($"sprite {slot} Y");
            await Assert
                .That(gb.DebugReadByte((ushort)(baseAddr + 1)))
                .IsEqualTo(x)
                .Because($"sprite {slot} X");
            await Assert
                .That(gb.DebugReadByte((ushort)(baseAddr + 2)))
                .IsEqualTo(tile)
                .Because($"sprite {slot} tile");
            await Assert
                .That(gb.DebugReadByte((ushort)(baseAddr + 3)))
                .IsEqualTo(attr)
                .Because($"sprite {slot} attr");
        }
    }

    // ---- Sprite OAM: positions genuinely advance with more completed iterations -------------------

    [Test]
    public async Task Oam_AfterMoreIterations_PositionsChangedAsExpected()
    {
        var rom = Compile(DemoSource);
        var gb = Boot(rom, HardwareMode.Dmg);
        int first = WaitForStableScore(gb, minSteps: 0, maxSteps: 2_000_000);
        var firstSprite0 = ExpectedSprite(first, 0);
        await Assert
            .That(gb.DebugReadByte(OamBase))
            .IsEqualTo(firstSprite0.y)
            .Because("sanity: first snapshot matches the formula");

        // Skip well past a handful more full orbit loop iterations (each iteration is on the order of
        // one vblank period) before polling again, so the second reading is guaranteed to observe later
        // iterations rather than re-observing the same stable state the first call already found.
        int second = WaitForStableScore(gb, minSteps: 200_000, maxSteps: 4_000_000);
        await Assert.That(second).IsGreaterThan(first);

        var (y, x, tile, attr) = ExpectedSprite(second, 0);
        await Assert
            .That(gb.DebugReadByte(OamBase))
            .IsEqualTo(y)
            .Because("sprite 0 Y after more frames");
        await Assert
            .That(gb.DebugReadByte((ushort)(OamBase + 1)))
            .IsEqualTo(x)
            .Because("sprite 0 X after more frames");
        await Assert.That(gb.DebugReadByte((ushort)(OamBase + 2))).IsEqualTo(tile);
        await Assert.That(gb.DebugReadByte((ushort)(OamBase + 3))).IsEqualTo(attr);
    }

    // ---- Palettes: the theme genuinely toggles every PaletteChangeFrames iterations ----------------

    [Test]
    public async Task Palette_TogglesAcrossASixtyIterationBoundary()
    {
        var rom = Compile(DemoSource);
        var gb = Boot(rom, HardwareMode.Dmg);
        int first = WaitForStableScore(gb, minSteps: 0, maxSteps: 2_000_000);
        bool firstAlt = ActiveThemeIsAlt(first);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo(firstAlt ? (byte)0x93 : (byte)0xE4);

        // Skip unconditionally past at least one PaletteChangeFrames boundary's worth of iterations
        // before polling again, so the second reading is guaranteed to land after a real theme switch,
        // not a re-observation of the first one — but comfortably short of 256 iterations, since
        // Video.FrameCount (what Game.cs's toggle actually gates on) is a WRAPPING byte, not the
        // unbounded iteration count this test derives from the score: past 255 the two diverge and
        // ActiveThemeIsAlt's formula (which assumes no wrap) would need its own modulo-256 correction.
        int second = WaitForStableScore(gb, minSteps: 1_200_000, maxSteps: 2_500_000);
        await Assert
            .That(second)
            .IsLessThan(256)
            .Because("stay clear of Video.FrameCount's byte wraparound");
        await Assert.That(second).IsGreaterThan(first + PaletteChangeFrames);

        bool secondAlt = ActiveThemeIsAlt(second);
        await Assert
            .That(secondAlt)
            .IsNotEqualTo(firstAlt)
            .Because("the theme must have toggled by now");
        await Assert
            .That(gb.DebugReadByte(0xFF47))
            .IsEqualTo(secondAlt ? (byte)0x93 : (byte)0xE4)
            .Because("BGP after the toggle");
    }

    // ---- Palettes: DMG quantized shades in BGP/OBP -------------------------------------------------

    [Test]
    public async Task Dmg_Run_HasQuantizedShadesInBgpAndObp()
    {
        var rom = Compile(DemoSource);
        var gb = Boot(rom, HardwareMode.Dmg);
        int completed = WaitForStableScore(gb, minSteps: 0, maxSteps: 2_000_000);
        bool alt = ActiveThemeIsAlt(completed);

        byte expectedBgp = alt ? (byte)0x93 : (byte)0xE4;
        byte expectedObp0 = alt ? (byte)0xE0 : (byte)0x1C;
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo(expectedBgp).Because("BGP");
        await Assert.That(gb.DebugReadByte(0xFF48)).IsEqualTo(expectedObp0).Because("OBP0");
    }

    // ---- Palettes: CGB RGB555 in palette RAM ---------------------------------------------------

    [Test]
    public async Task Cgb_Run_HasRgb555InPaletteRam()
    {
        var rom = Compile(DemoSource);
        var gb = Boot(rom, HardwareMode.Cgb);
        int completed = WaitForStableScore(gb, minSteps: 0, maxSteps: 2_000_000);
        bool alt = ActiveThemeIsAlt(completed);

        (ushort c0, ushort c1, ushort c2, ushort c3) bg = alt
            ? (
                Koh.GameBoy.Graphics.Rgb.White,
                Koh.GameBoy.Graphics.Rgb.Make(0, 20, 25),
                Koh.GameBoy.Graphics.Rgb.Make(0, 8, 14),
                Koh.GameBoy.Graphics.Rgb.Black
            )
            : (
                Koh.GameBoy.Graphics.Rgb.White,
                Koh.GameBoy.Graphics.Rgb.Make(20, 25, 20),
                Koh.GameBoy.Graphics.Rgb.Make(8, 14, 8),
                Koh.GameBoy.Graphics.Rgb.Black
            );
        await Assert.That(gb.Ppu.BgPalette.GetColor(0, 0)).IsEqualTo(bg.c0);
        await Assert.That(gb.Ppu.BgPalette.GetColor(0, 1)).IsEqualTo(bg.c1);
        await Assert.That(gb.Ppu.BgPalette.GetColor(0, 2)).IsEqualTo(bg.c2);
        await Assert.That(gb.Ppu.BgPalette.GetColor(0, 3)).IsEqualTo(bg.c3);

        (ushort c1, ushort c2, ushort c3) obj = alt
            ? (
                Koh.GameBoy.Graphics.Rgb.Yellow,
                Koh.GameBoy.Graphics.Rgb.Blue,
                Koh.GameBoy.Graphics.Rgb.Black
            )
            : (
                Koh.GameBoy.Graphics.Rgb.White,
                Koh.GameBoy.Graphics.Rgb.Make(31, 0, 0),
                Koh.GameBoy.Graphics.Rgb.Black
            );
        await Assert.That(gb.Ppu.ObjPalette.GetColor(0, 1)).IsEqualTo(obj.c1);
        await Assert.That(gb.Ppu.ObjPalette.GetColor(0, 2)).IsEqualTo(obj.c2);
        await Assert.That(gb.Ppu.ObjPalette.GetColor(0, 3)).IsEqualTo(obj.c3);
    }
}
