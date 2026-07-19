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

    private static IrModule Frontend(OptimizationLevel level, DiagnosticBag diagnostics)
    {
        var trees = Sources.Value.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
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

    // Assets.Load: 3 terrain tiles + two 8-tile figure sheets (ids 0..18), font at 19.
    // Glyph tile = 19 + (char - ' ').
    private static byte GlyphTile(char c) => (byte)(19 + (c - ' '));

    private static byte MapTile(GameBoySystem gb, int col, int row) =>
        gb.DebugReadByte((ushort)(0x9800 + row * 32 + col));

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

    private static bool InBattle(GameBoySystem gb) => TextAt(gb, 2, 10, "ATTACK");

    private static bool OnOverworld(GameBoySystem gb) => TextAt(gb, 0, 0, "HP");

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
        WaitFor(
            gb,
            () =>
                MapTile(gb, 0, 2) == 1 // the dirty range drains in address order, so sample
                && MapTile(gb, 16, 4) == 2 // cells across the WHOLE map, not just early rows
                && MapTile(gb, 10, 4) == 5
                && MapTile(gb, 0, 16) == 1
                && MapTile(gb, 2, 4) == 3
                && TextAt(gb, 0, 0, "HP"),
            900,
            "overworld fully drawn"
        );

        // Overworld: HUD, terrain, hero, villager all on the hardware tilemap. World cells are
        // 16x16 (2x2 tiles), cell (cx,cy) -> tile (cx*2, cy*2+2); a figure's assert samples its
        // TOP-LEFT tile id.
        AssertText(gb, 0, 0, "HP");
        AssertText(gb, 6, 0, "LV");
        await Assert.That(MapTile(gb, 0, 2)).IsEqualTo((byte)1); // wall ring, cell (0,0)
        await Assert.That(MapTile(gb, 16, 4)).IsEqualTo((byte)2); // water, cell (8,1)
        await Assert.That(MapTile(gb, 2, 4)).IsEqualTo((byte)3); // hero TL, cell (1,1)
        await Assert.That(MapTile(gb, 3, 4)).IsEqualTo((byte)4); // hero TR
        await Assert.That(MapTile(gb, 2, 5)).IsEqualTo((byte)7); // hero BL
        await Assert.That(MapTile(gb, 10, 4)).IsEqualTo((byte)5); // villager TL, cell (5,1)

        // CGB palette RAM holds the PNG-authored colors (water palette = slot 2, color 1).
        var water = Sheets.Value.Single(s => s.Name == "Water");
        gb.Ppu.BgPalette.IndexRegister = (byte)(2 * 8 + 1 * 2);
        var low = gb.Ppu.BgPalette.ReadData();
        gb.Ppu.BgPalette.IndexRegister = (byte)(2 * 8 + 1 * 2 + 1);
        var high = gb.Ppu.BgPalette.ReadData();
        await Assert.That((ushort)(low | (high << 8))).IsEqualTo(water.Rgb555Palette[1]);

        // Walk right from cell (1,1) to (4,1) — beside the villager at (5,1) — playing through
        // any encounter along the way.
        for (var i = 0; i < 3; i++)
            Step(gb, JoypadButton.Right);
        await Assert.That(MapTile(gb, 8, 4)).IsEqualTo((byte)3); // hero TL arrived at cell (4,1)

        // Talk: the dialogue box opens with the first line...
        Press(gb, JoypadButton.A);
        WaitFor(gb, () => TextAt(gb, 1, 14, "WELCOME TRAVELER!"), 300, "dialogue line 1");

        // ...pages twice more, and the stored close-callback (enabler E3) fires, landing back on
        // the overworld with the hero where they stood.
        Press(gb, JoypadButton.A);
        WaitFor(gb, () => TextAt(gb, 1, 14, "MONSTERS ROAM THE"), 300, "dialogue line 2");
        Press(gb, JoypadButton.A);
        WaitFor(gb, () => TextAt(gb, 1, 14, "GRASS. LEVEL UP!"), 300, "dialogue line 3");
        Press(gb, JoypadButton.A);
        WaitFor(
            gb,
            () => TextAt(gb, 0, 0, "HP") && MapTile(gb, 8, 4) == 3,
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
        WaitFor(
            gb,
            () =>
                MapTile(gb, 10, 4) == 5
                && MapTile(gb, 10, 5) == 9 // figure BOTTOM rows too — the shadow drains in
                && MapTile(gb, 2, 4) == 3 // address order, and attributes drain behind tiles
                && MapTile(gb, 2, 5) == 7
                && TextAt(gb, 0, 0, "HP"),
            900,
            "overworld drawn"
        );
        RunFrames(gb, 150); // let the CGB attribute shadow fully drain before shooting
        Capture(gb, Path.Combine(outDir, "overworld.png"));

        // Walk to the villager, shoot the dialogue.
        for (var i = 0; i < 3; i++)
            Step(gb, JoypadButton.Right);
        Press(gb, JoypadButton.A);
        WaitFor(gb, () => TextAt(gb, 1, 14, "WELCOME TRAVELER!"), 300, "dialogue open");
        RunFrames(gb, 60);
        Capture(gb, Path.Combine(outDir, "dialogue.png"));
        Press(gb, JoypadButton.A);
        Press(gb, JoypadButton.A);
        Press(gb, JoypadButton.A);
        WaitFor(gb, () => TextAt(gb, 0, 0, "HP"), 900, "back on the overworld");

        // Pace until an encounter fires, and shoot the battle screen.
        for (var wander = 0; wander < 200 && !InBattle(gb); wander++)
        {
            Press(gb, wander % 2 == 0 ? JoypadButton.Down : JoypadButton.Up);
            RunFrames(gb, 15);
        }
        WaitFor(gb, () => InBattle(gb), 300, "an encounter");
        WaitFor(gb, () => MapTile(gb, 9, 5) is 11 or 13, 300, "monster drawn");
        RunFrames(gb, 90); // monster bottom row + attributes settle
        Capture(gb, Path.Combine(outDir, "battle.png"));

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
