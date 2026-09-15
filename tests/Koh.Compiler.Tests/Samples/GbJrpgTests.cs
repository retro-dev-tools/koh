using System.Collections.Immutable;
using Koh.Build.Tasks;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Joypad;
using Koh.Emulator.Core.Ppu;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Samples;

/// <summary>
/// Acceptance test of the SECOND north star (M6): <c>samples/gb-jrpg</c> — a Game Boy COLOR
/// mini-JRPG written as ideal C# before its constructs could compile. The gaps it forced, now
/// fixed: rank-2 rectangular arrays (<c>byte[,]</c> overworld), reference-element arrays
/// (<c>string[]</c> dialogue), and stored delegates (the dialogue close-callback, enabler E3).
/// Art comes from the PNG tile pipeline (no byte arrays in source): this harness runs the same
/// <see cref="TileSheetConverter"/> the SDK build runs over <c>art/*.png</c> and injects the
/// generated <c>Art</c> source into the Roslyn compilation, exactly like <c>Sdk.targets</c> does.
///
/// The play test boots CGB, walks the hero to the villager (fighting through any random
/// encounter the deterministic run happens to roll — encounters are the game, not noise), pages
/// the dialogue, and asserts the close-callback lands back on the overworld; palette RAM is
/// asserted against the PNG-authored colors. The game is CGB-EXCLUSIVE (header 0xC0, no
/// monochrome fallback) — asserted directly on the linked ROM.
/// </summary>
public class GbJrpgTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("could not locate the repository root (Koh.slnx).");
        return dir.FullName;
    }

    private static readonly Lazy<string> SampleDir = new(() =>
        Path.Combine(RepoRoot(), "samples", "gb-jrpg")
    );

    private static readonly Lazy<IReadOnlyList<TileSheetConverter.Sheet>> Sheets = new(() =>
        Directory
            .GetFiles(Path.Combine(SampleDir.Value, "art"), "*.png")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(TileSheetConverter.Convert)
            .ToArray()
    );

    private static readonly Lazy<IReadOnlyList<string>> Sources = new(() =>
    {
        var sources = Directory
            .GetFiles(SampleDir.Value, "*.cs")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .ToList();
        // The same generated Art source the SDK's KohGenerateTileSheets target injects.
        sources.Add(TileSheetConverter.GenerateSource("Koh.Samples.GbJrpg", Sheets.Value));
        return sources;
    });

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
        "koh-gbjrpg-tests"
    );

    // The Koh SDK brings Koh.GameBoy into scope everywhere via a global <Using> (see Sdk.props) so
    // game files need no `using` of their own; compiling the sample files directly with Roslyn (no
    // SDK) needs the same global using injected explicitly.
    private const string GlobalUsings = "global using Koh.GameBoy;\n";

    private static IrModule Frontend(OptimizationLevel level, DiagnosticBag diagnostics)
    {
        var trees = Sources
            .Value.Append(GlobalUsings)
            .Select(s => CSharpSyntaxTree.ParseText(s))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "GbJrpgAsm_" + Guid.NewGuid().ToString("N"),
            trees,
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"gbjrpg_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        var input = CompilerInput.FromAssembly(
            path,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        return new CilFrontend().Lower(input, diagnostics);
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Sample_CompilesUnmodified_NoDiagnostics_ValidIr(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    // ---- Emulator play test ---------------------------------------------------------------------

    private static GameBoySystem Boot(HardwareMode mode)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(OptimizationLevel.Release, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        IrOptimizer.Optimize(module);
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        // CGB-only, exactly as the sample's csproj builds it (KohCgbOnly=true).
        var link = new LinkerType().Link(
            [new LinkerInput("jrpg", model)],
            new LinkOptions(CgbOnly: true)
        );
        var rom =
            link.RomData
            ?? throw new InvalidOperationException(
                "no ROM; linker diagnostics:\n  "
                    + string.Join("\n  ", link.Diagnostics.Select(d => d.Message))
            );
        var gb = new GameBoySystem(mode, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = 0x100;
        return gb;
    }

    private static void RunFrames(GameBoySystem gb, int frames)
    {
        for (var i = 0; i < frames; i++)
            gb.RunFrame();
    }

    // Assets.Load: 7 sheets (grass/wall/water/tree/chars/monsters/ui, ids 0..45), font at 46.
    // Glyph tile = 46 + (char - ' ').
    private static byte GlyphTile(char c) => (byte)(46 + (c - ' '));

    private static byte MapTile(GameBoySystem gb, int col, int row) =>
        gb.DebugReadByte((ushort)(0x9800 + row * 32 + col));

    // Hero OAM: 4 hardware OBJs (shadow slots 0-3, Pan Docs entry order Y/X/Tile/Attr) instead of
    // BG tiles — Sprites.Flush DMAs the whole shadow in one shot inside Video.EndFrame, so a move
    // is an O(1) OAM write, not the ~8-tile vblank drip that used to ghost across frames.
    private const int OamY = 0,
        OamX = 1,
        OamTile = 2,
        OamAttr = 3;

    private static byte OamByte(GameBoySystem gb, int slot, int field) =>
        gb.DebugReadByte((ushort)(0xFE00 + slot * 4 + field));

    private static bool TextAt(GameBoySystem gb, int col, int row, string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (MapTile(gb, col + i, row) != GlyphTile(text[i]))
                return false;
        return true;
    }

    private static void AssertText(GameBoySystem gb, int col, int row, string text)
    {
        if (!TextAt(gb, col, row, text))
        {
            var actual = string.Join(
                " ",
                Enumerable.Range(0, text.Length).Select(i => MapTile(gb, col + i, row))
            );
            throw new InvalidOperationException(
                $"expected '{text}' at ({col},{row}); map holds [{actual}]"
            );
        }
    }

    private static bool InBattle(GameBoySystem gb) => TextAt(gb, 4, 10, "ATTACK");

    private static bool OnOverworld(GameBoySystem gb) => TextAt(gb, 1, 0, "HP");

    private static bool OnTitle(GameBoySystem gb) => MapTile(gb, 5, 5) == GlyphTile('T');

    private static void Press(GameBoySystem gb, JoypadButton button, int settle = 8)
    {
        gb.JoypadPress(button);
        RunFrames(gb, settle);
        gb.JoypadRelease(button);
        RunFrames(gb, settle);
    }

    /// <summary>Fight out any random encounter (attack until it resolves) so a walk can continue.
    /// Encounters are the game's own behavior on a deterministic seed — the test plays through
    /// them rather than pretending they don't exist.</summary>
    private static void EnsureOverworld(GameBoySystem gb)
    {
        for (var round = 0; round < 24 && !OnOverworld(gb); round++)
        {
            if (InBattle(gb))
                Press(gb, JoypadButton.A); // cursor starts on ATTACK
            RunFrames(gb, 30);
        }
        if (!OnOverworld(gb))
            throw new InvalidOperationException("could not return to the overworld");
    }

    private static void Step(GameBoySystem gb, JoypadButton dir)
    {
        Press(gb, dir);
        RunFrames(gb, 20);
        EnsureOverworld(gb);
    }

    /// <summary>Condition-driven pacing: the deferred Bg flush drains a full map (tiles AND, on
    /// CGB, attributes) over an LCD-on-budget number of vblanks, so fixed frame counts are
    /// timing-brittle — run until the observable state lands instead, with a hard budget.</summary>
    private static void WaitFor(GameBoySystem gb, Func<bool> condition, int maxFrames, string what)
    {
        for (var frame = 0; frame < maxFrames; frame += 10)
        {
            if (condition())
                return;
            RunFrames(gb, 10);
        }
        if (!condition())
            throw new InvalidOperationException($"'{what}' not reached within {maxFrames} frames");
    }

    [Test]
    public async Task Sample_CgbPlaythrough_WalksTalksAndCallsBack()
    {
        var gb = Boot(HardwareMode.Cgb);

        // Title card first: wait for its glyphs, then press Start to hand off to the overworld.
        WaitFor(gb, () => OnTitle(gb), 300, "title screen drawn");
        Press(gb, JoypadButton.Start);

        WaitFor(
            gb,
            () =>
                MapTile(gb, 12, 2) == 8 // the dirty range drains in address order, so sample
                && MapTile(gb, 16, 6) == 6 // cells across the WHOLE map, not just early rows
                && MapTile(gb, 10, 4) == 14
                && MapTile(gb, 12, 16) == 8
                && OamByte(gb, 0, OamTile) == 12 // hero OBJs are a same-frame write, not a drip
                && TextAt(gb, 1, 0, "HP"),
            900,
            "overworld fully drawn"
        );

        // Overworld: HUD, terrain, villager all on the hardware tilemap; the hero is 4 hardware
        // OBJs (shadow OAM slots 0-3), not tilemap tiles. World cells are 16x16 (2x2 tiles); a BG
        // figure's assert samples its TOP-LEFT tile id, while the hero's four OBJ slots are
        // TL/TR/BL/BR in that order. Cell (1,1) -> screen px (16,32) -> OAM (X+8,Y+16).
        AssertText(gb, 1, 0, "HP");
        AssertText(gb, 8, 0, "LV");
        await Assert.That(MapTile(gb, 12, 2)).IsEqualTo((byte)8); // tree, cell (6,0)
        await Assert.That(MapTile(gb, 16, 6)).IsEqualTo((byte)6); // water, cell (8,2)
        await Assert.That(MapTile(gb, 10, 4)).IsEqualTo((byte)14); // villager TL, cell (5,1)

        await Assert.That(OamByte(gb, 0, OamY)).IsEqualTo((byte)48); // hero TL, cell (1,1)
        await Assert.That(OamByte(gb, 0, OamX)).IsEqualTo((byte)24);
        await Assert.That(OamByte(gb, 0, OamTile)).IsEqualTo((byte)12);
        await Assert.That(OamByte(gb, 1, OamY)).IsEqualTo((byte)48); // hero TR
        await Assert.That(OamByte(gb, 1, OamX)).IsEqualTo((byte)32);
        await Assert.That(OamByte(gb, 1, OamTile)).IsEqualTo((byte)13);
        await Assert.That(OamByte(gb, 2, OamY)).IsEqualTo((byte)56); // hero BL
        await Assert.That(OamByte(gb, 2, OamX)).IsEqualTo((byte)24);
        await Assert.That(OamByte(gb, 2, OamTile)).IsEqualTo((byte)16);
        await Assert.That(OamByte(gb, 3, OamY)).IsEqualTo((byte)56); // hero BR
        await Assert.That(OamByte(gb, 3, OamX)).IsEqualTo((byte)32);
        await Assert.That(OamByte(gb, 3, OamTile)).IsEqualTo((byte)17);

        // No-ghost regression: unlike the old BG figure (~8 queued tile rewrites draining over
        // several vblanks), a sprite move is one shadow-OAM write flushed whole by the next
        // Video.EndFrame — so 2 frames after the input registers (1 frame for Input.Update to
        // latch the press, 1 more for Update to move the sprite and EndFrame to flush it) the OAM
        // X has already advanced by exactly one cell, and the vacated cell was never touched at
        // all (no restore-then-redraw two-step), so it holds only terrain (<= 3, grass variants),
        // never a hero tile (12/13/16/17).
        byte xBefore = OamByte(gb, 0, OamX);
        Press(gb, JoypadButton.Right, settle: 2); // 2 frames: 1 to latch the input, 1 to move+flush
        await Assert.That((byte)(OamByte(gb, 0, OamX) - xBefore)).IsEqualTo((byte)16);
        await Assert.That(MapTile(gb, 2, 4)).IsLessThanOrEqualTo((byte)3);
        await Assert.That(MapTile(gb, 3, 4)).IsLessThanOrEqualTo((byte)3);
        await Assert.That(MapTile(gb, 2, 5)).IsLessThanOrEqualTo((byte)3);
        await Assert.That(MapTile(gb, 3, 5)).IsLessThanOrEqualTo((byte)3);
        EnsureOverworld(gb); // play out any encounter this move triggered before continuing

        // CGB palette RAM holds the PNG-authored colors (water palette = slot 2, color 1).
        var water = Sheets.Value.Single(s => s.Name == "Water");
        gb.Ppu.BgPalette.IndexRegister = (byte)(2 * 8 + 1 * 2);
        var low = gb.Ppu.BgPalette.ReadData();
        gb.Ppu.BgPalette.IndexRegister = (byte)(2 * 8 + 1 * 2 + 1);
        var high = gb.Ppu.BgPalette.ReadData();
        await Assert.That((ushort)(low | (high << 8))).IsEqualTo(water.Rgb555Palette[1]);

        // Walk right from cell (2,1) (the no-ghost step above already covered (1,1) -> (2,1)) to
        // (4,1) — beside the villager at (5,1) — playing through any encounter along the way.
        for (var i = 0; i < 2; i++)
            Step(gb, JoypadButton.Right);
        await Assert.That(OamByte(gb, 0, OamY)).IsEqualTo((byte)48); // hero TL arrived at cell (4,1)
        await Assert.That(OamByte(gb, 0, OamX)).IsEqualTo((byte)72);
        await Assert.That(OamByte(gb, 0, OamTile)).IsEqualTo((byte)12);

        // Talk: the dialogue box opens with the first line...
        Press(gb, JoypadButton.A);
        WaitFor(gb, () => TextAt(gb, 1, 14, "THIS IS MILLMERE."), 300, "dialogue line 1");

        // ...pages twice more, and the stored close-callback (enabler E3) fires, landing back on
        // the overworld with the hero where they stood.
        Press(gb, JoypadButton.A);
        WaitFor(gb, () => TextAt(gb, 1, 14, "SLIMES BY THE POND"), 300, "dialogue line 2");
        Press(gb, JoypadButton.A);
        WaitFor(gb, () => TextAt(gb, 1, 14, "THE OLD MILL FELL."), 300, "dialogue line 3");
        Press(gb, JoypadButton.A);
        WaitFor(
            gb,
            () =>
                TextAt(gb, 1, 0, "HP")
                && OamByte(gb, 0, OamTile) == 12
                && OamByte(gb, 0, OamX) == 72,
            900,
            "close callback returned to the overworld"
        );
    }

    // ---- Acceptance screenshots: real CGB framebuffer captures at the story beats ---------------

    [Test]
    public async Task Sample_CaptureAcceptanceScreenshots()
    {
        var outDir = Path.Combine(SampleDir.Value, "screenshots");
        Directory.CreateDirectory(outDir);

        var gb = Boot(HardwareMode.Cgb);

        // Title card: the framed window, mascot, and "PRESS START" — shoot it before advancing.
        WaitFor(
            gb,
            () =>
                MapTile(gb, 5, 5) == GlyphTile('T') // "TINY  QUEST"
                && MapTile(gb, 5, 12) == GlyphTile('P') // "PRESS START"
                && MapTile(gb, 9, 9) == 20, // slime mascot TL (SlimeTile)
            300,
            "title screen drawn"
        );
        RunFrames(gb, 30); // let the CGB attribute shadow fully drain before shooting
        Capture(gb, Path.Combine(outDir, "title.png"));
        Press(gb, JoypadButton.Start);

        WaitFor(
            gb,
            () =>
                MapTile(gb, 10, 4) == 14
                && MapTile(gb, 10, 5) == 18 // villager figure BOTTOM row too — the shadow drains
                // in address order, and attributes drain behind tiles; the hero is an OBJ now, so
                // its OAM write is same-frame, not part of this drip.
                && OamByte(gb, 0, OamTile) == 12
                && TextAt(gb, 1, 0, "HP"),
            900,
            "overworld drawn"
        );
        RunFrames(gb, 30); // let the CGB attribute shadow fully drain before shooting
        Capture(gb, Path.Combine(outDir, "overworld.png"));

        // Walk to the villager, shoot the dialogue.
        for (var i = 0; i < 3; i++)
            Step(gb, JoypadButton.Right);
        Press(gb, JoypadButton.A);
        WaitFor(gb, () => TextAt(gb, 1, 14, "THIS IS MILLMERE."), 300, "dialogue open");
        RunFrames(gb, 60);
        Capture(gb, Path.Combine(outDir, "dialogue.png"));
        Press(gb, JoypadButton.A);
        Press(gb, JoypadButton.A);
        Press(gb, JoypadButton.A);
        WaitFor(gb, () => TextAt(gb, 1, 0, "HP"), 900, "back on the overworld");

        // Pace until an encounter fires, and shoot the battle screen.
        for (var wander = 0; wander < 200 && !InBattle(gb); wander++)
        {
            Press(gb, wander % 2 == 0 ? JoypadButton.Down : JoypadButton.Up);
            RunFrames(gb, 15);
        }
        WaitFor(gb, () => InBattle(gb), 300, "an encounter");
        // Monster BL tile — one of SlimeTile/BatTile/GhostTile/DrakeTile + 4, whichever spawned.
        WaitFor(gb, () => MapTile(gb, 9, 5) is 24 or 26 or 32 or 34, 300, "monster drawn");
        RunFrames(gb, 90); // monster bottom row + attributes settle
        Capture(gb, Path.Combine(outDir, "battle.png"));

        await Assert.That(File.Exists(Path.Combine(outDir, "title.png"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(outDir, "overworld.png"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(outDir, "dialogue.png"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(outDir, "battle.png"))).IsTrue();
    }

    private static void Capture(GameBoySystem gb, string path)
    {
        gb.RunFrame(); // one more frame so the front buffer holds the settled scene
        var fb = gb.Framebuffer.FrontArray;
        var rgb = new byte[Framebuffer.Width * Framebuffer.Height * 3];
        for (var p = 0; p < Framebuffer.Width * Framebuffer.Height; p++)
        {
            rgb[p * 3 + 0] = fb[p * 4 + 0];
            rgb[p * 3 + 1] = fb[p * 4 + 1];
            rgb[p * 3 + 2] = fb[p * 4 + 2];
        }
        TestSupport.TestPng.WriteRgb(path, Framebuffer.Width, Framebuffer.Height, rgb, scale: 3);
    }

    [Test]
    public async Task Sample_RomHeader_IsCgbExclusive()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(OptimizationLevel.Release, diagnostics);
        IrOptimizer.Optimize(module);
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var link = new LinkerType().Link(
            [new LinkerInput("jrpg", model)],
            new LinkOptions(CgbOnly: true)
        );
        // $0143 = 0xC0: CGB EXCLUSIVE — a DMG refuses to boot it; there is no monochrome fallback.
        await Assert.That(link.RomData![0x143]).IsEqualTo((byte)0xC0);
    }
}
