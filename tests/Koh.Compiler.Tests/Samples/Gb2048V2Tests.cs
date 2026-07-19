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
using Koh.Emulator.Core.Joypad;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Samples;

/// <summary>
/// THE acceptance test of the ideal-game-API program
/// (<c>docs/superpowers/specs/2026-07-19-ideal-game-api-design.md</c>, milestone M5): the
/// north-star sample <c>samples/gb-2048-v2</c> — written FIRST, as ideal C# (scene classes with
/// overridden Update, <c>Game.Run</c>, struct-by-value <c>Line</c> returns, count-free
/// <c>TileAsset.Define</c>, <c>Input.Repeated</c> sliding), before any of it could compile — now
/// compiles UNMODIFIED through Roslyn → CIL frontend → verifier → SM83 → linker, boots on the
/// emulator through the real reset vector, and plays: title screen, Start into the play scene,
/// scripted d-pad slides spawning tiles.
///
/// Assertions read the hardware tilemap (glyph indexes Text drew through the deferred Bg shadow,
/// board quads BoardView filled) — robust facts of the game, not pixel-exact layouts, though the
/// run itself is fully deterministic (Game.Boot seeds Rng from the emulator's deterministic DIV).
/// </summary>
public class Gb2048V2Tests
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

    private static readonly Lazy<IReadOnlyList<string>> SampleSources = new(() =>
    {
        var dir = Path.Combine(RepoRoot(), "samples", "gb-2048-v2");
        return Directory
            .GetFiles(dir, "*.cs")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .ToArray();
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
        "koh-gb2048v2-tests"
    );

    private static string CompileToAssembly(OptimizationLevel level)
    {
        var trees = SampleSources.Value.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
        var compilation = CSharpCompilation.Create(
            "Gb2048V2Asm_" + Guid.NewGuid().ToString("N"),
            trees,
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"gb2048v2_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    private static IrModule Frontend(OptimizationLevel level, DiagnosticBag diagnostics)
    {
        var assemblyPath = CompileToAssembly(level);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        return new CilFrontend().Lower(input, diagnostics);
    }

    // ---- Fixture 1: the whole sample lowers with zero diagnostics, valid IR, both configs -----

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

    // ---- Fixture 2: boots and plays (Release — CLAUDE.md's timing-fixture rule) ----------------

    private static GameBoySystem BootRom()
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
        var link = new LinkerType().Link([new LinkerInput("2048v2", model)]);
        var rom =
            link.RomData
            ?? throw new InvalidOperationException(
                "no ROM; linker diagnostics:\n  "
                    + string.Join("\n  ", link.Diagnostics.Select(d => d.Message))
            );
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = 0x100; // the real reset-vector boot path
        return gb;
    }

    private static void RunFrames(GameBoySystem gb, int frames)
    {
        for (var i = 0; i < frames; i++)
            gb.RunFrame();
    }

    // Assets.Load puts the 12-tile board art at VRAM slot 0 and the font right after it
    // (FontBase = Tiles.TileCount = 12); a glyph's tile index is FontBase + (char - ' ').
    private static byte GlyphTile(char c) => (byte)(12 + (c - ' '));

    private static byte MapTile(GameBoySystem gb, int col, int row) =>
        gb.DebugReadByte((ushort)(0x9800 + row * 32 + col));

    private static void AssertText(GameBoySystem gb, int col, int row, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var actual = MapTile(gb, col + i, row);
            if (actual != GlyphTile(text[i]))
                throw new InvalidOperationException(
                    $"expected '{text}' at ({col},{row}); char {i} ('{text[i]}') should be tile "
                        + $"{GlyphTile(text[i])} but the map holds {actual}"
                );
        }
    }

    /// <summary>Nonzero cells on the drawn board (each cell is a 2x2 quad of its exponent's tile at
    /// BoardView's grid: col 3+c*4, row 3+r*3).</summary>
    private static int CountBoardCells(GameBoySystem gb)
    {
        var count = 0;
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
            if (MapTile(gb, 3 + c * 4, 3 + r * 3) != 0)
                count++;
        return count;
    }

    private static void Press(GameBoySystem gb, JoypadButton button)
    {
        gb.JoypadPress(button);
        RunFrames(gb, 6); // enough frames for Input.Update to latch the edge and the scene to act
        gb.JoypadRelease(button);
        RunFrames(gb, 6); // release settles; next press is a fresh edge
    }

    [Test]
    public async Task Sample_BootsToTitle_StartsGame_SlidesSpawnTiles()
    {
        var gb = BootRom();

        // Boot -> TitleScene.Enter draws through the deferred Bg shadow. Boot itself is slow (the
        // LCD-off authoring pass: 96 font tiles + board art + full map/OAM clears in Release SM83),
        // and the DMG-budgeted flush drains over several vblanks after Video.Start — measured
        // stable by frame ~120.
        RunFrames(gb, 150);
        AssertText(gb, 8, 7, "2048");
        AssertText(gb, 4, 10, "PRESS START");

        // Start: deferred scene commit -> Title.Exit clears the map (a full-map dirty range that
        // drains over several vblanks) -> PlayScene.Enter draws the board and score label.
        Press(gb, JoypadButton.Start);
        RunFrames(gb, 90);
        AssertText(gb, 1, 0, "SCORE");
        await Assert.That(CountBoardCells(gb)).IsEqualTo(2); // Board.Reset spawns exactly two

        // Eight scripted slides. Every slide that moves anything spawns a tile (merges can reduce
        // the count again), so the population walks upward from 2 without ever exceeding the board.
        var before = CountBoardCells(gb);
        Press(gb, JoypadButton.Left);
        RunFrames(gb, 30);
        var afterFirst = CountBoardCells(gb);
        await Assert.That(afterFirst >= 2 && afterFirst <= before + 1).IsTrue();

        JoypadButton[] script =
        [
            JoypadButton.Up,
            JoypadButton.Right,
            JoypadButton.Down,
            JoypadButton.Left,
            JoypadButton.Up,
            JoypadButton.Right,
            JoypadButton.Down,
        ];
        foreach (var button in script)
        {
            Press(gb, button);
            RunFrames(gb, 30);
        }

        var final = CountBoardCells(gb);
        await Assert.That(final).IsGreaterThan(2); // the game demonstrably played
        await Assert.That(final).IsLessThanOrEqualTo(16);
        AssertText(gb, 1, 0, "SCORE"); // HUD survived the redraws
    }
}
