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

namespace Koh.Compiler.Tests.Samples;

/// <summary>
/// The CIL-frontend counterpart of <see cref="Game2048Tests"/>: instead of feeding the sample's C#
/// source straight to <c>CSharpFrontend</c>, this compiles the real <c>samples/gb-2048-cs</c> files
/// (unmodified) to a genuine assembly with Roslyn — referencing the already-built
/// <c>Koh.GameBoy.dll</c> exactly as the SDK's <c>cil</c> path does (see <c>Sdk.targets</c>'s
/// <c>CompileKohRom</c>: <c>AssemblyPath</c> + <c>ReferencePaths</c>, no source list) — then runs that
/// assembly through <see cref="CilFrontend"/> -&gt; IR -&gt; verifier -&gt; SM83 backend -&gt; linker -&gt;
/// <see cref="GameBoySystem"/>. Proves the game genuinely works when the CIL frontend lowers a compiled
/// assembly plus the framework's Koh.GameBoy.dll on demand, not just that it compiles.
/// </summary>
public class CilGame2048Tests
{
    // ---- Real sample sources, read once ----------------------------------------------------

    private static readonly string BoardSource = ReadSampleFile("Board.cs");
    private static readonly string TilesSource = ReadSampleFile("Tiles.cs");
    private static readonly string GameSource = ReadSampleFile("Game.cs");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("could not locate the repository root (Koh.slnx).");
        return dir.FullName;
    }

    private static string ReadSampleFile(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "samples", "gb-2048-cs", name));

    // ---- Roslyn: compile the real sample files (+ an optional test entry point) to a real
    // assembly on disk, referencing Koh.GameBoy.dll exactly as the SDK's cil path does. ------------

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
        "koh-cil-game2048-tests"
    );

    // The Koh SDK brings Koh.GameBoy into scope everywhere via a global <Using> (see Sdk.props) so
    // game files need no `using` of their own; compiling the sample files directly with Roslyn (no
    // SDK) needs the same global using injected explicitly.
    private const string GlobalUsings = "global using Koh.GameBoy;\n";

    private static string CompileToAssembly(IReadOnlyList<string> sources, OptimizationLevel level)
    {
        var trees = sources
            .Append(GlobalUsings)
            .Select(s => CSharpSyntaxTree.ParseText(s))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "CilGame2048Asm_" + Guid.NewGuid().ToString("N"),
            trees,
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_2048_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    // ---- Frontend -> IR, verified -----------------------------------------------------------

    private static IrModule Frontend(
        IReadOnlyList<string> sources,
        OptimizationLevel level,
        DiagnosticBag diagnostics
    )
    {
        var assemblyPath = CompileToAssembly(sources, level);
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

    private static EmitModel Compile(IReadOnlyList<string> sources, bool optimize = false)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(sources, OptimizationLevel.Debug, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        if (optimize)
            IrOptimizer.Optimize(module);
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    // ---- Emulator harness --------------------------------------------------------------------

    private static ushort Run(string testMain, bool optimize = false)
    {
        // Board.cs + Tiles.cs (unmodified) plus a synthetic entry point in the same namespace, in
        // place of the real Game.cs's Main (mirrors Game2048Tests.Run's GameLibrary + injected Main).
        var sources = new[] { BoardSource, TilesSource, testMain };
        var model = Compile(sources, optimize);
        var rom = new LinkerType().Link([new LinkerInput("t", model)]).RomData!;

        int codeStart = Sm83Backend.CodeBase;
        int codeEnd = codeStart + model.Sections[0].Data.Length;
        // The single-bank backend emits functions in `IrModule.Functions` order (Board's/Tiles's
        // methods first, the synthetic TestEntry.Main wherever it lands after them) — CodeBase is only
        // the entry's address by construction when the ROM boots for real, through the header's reset
        // vector at 0x100 (NOP; JP <entry>, entry resolved to whatever address the entry function
        // actually got — see Sm83Backend's `entryAddress` local). A real ROM always boots through that
        // indirection; this harness starts execution straight at the entry's own code instead, so it has
        // to look the address up the same way rather than assume it equals CodeBase.
        var entrySymbol = model.Symbols.Single(s => s.Name == "TestEntry.Main");
        int entryPc = codeStart + (int)entrySymbol.Value;

        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)entryPc;
        for (int steps = 0; steps < 1_000_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < codeStart || pc >= codeEnd)
                break;
            gb.StepInstruction();
        }
        return gb.Registers.HL;
    }

    private static string TestEntry(string body) =>
        $$"""
            namespace Koh.Samples.Gb2048CSharp;

            static unsafe class TestEntry
            {
                static ushort Main()
                {
                    {{body}}
                }
            }
            """;

    // ---- The real sample compiles through the CIL frontend to a valid, bootable ROM -----------

    [Test]
    public async Task Sample_CompilesWithoutDiagnostics()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(
            [BoardSource, TilesSource, GameSource],
            OptimizationLevel.Debug,
            diagnostics
        );
        new Sm83Backend().Compile(module, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
    }

    [Test]
    public async Task Sample_LinksToBootableRom()
    {
        var model = Compile([BoardSource, TilesSource, GameSource]);
        var rom = new LinkerType().Link([new LinkerInput("2048", model)]).RomData!;

        await Assert.That(rom[0x100]).IsEqualTo((byte)0x00); // NOP
        await Assert.That(rom[0x101]).IsEqualTo((byte)0xC3); // JP a16
        await Assert.That(rom[0x104]).IsEqualTo((byte)0xCE); // first byte of the Nintendo logo
        await Assert.That(rom[0x105]).IsEqualTo((byte)0xED);
    }

    [Test]
    public async Task Sample_RomBootsIntoMainAndInitializes()
    {
        var model = Compile([BoardSource, TilesSource, GameSource]);
        var rom = new LinkerType().Link([new LinkerInput("2048", model)]).RomData!;

        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Pc = 0x100;
        gb.Registers.Sp = 0xFFFE;
        for (int i = 0; i < 2_000_000; i++)
            gb.StepInstruction();

        await Assert.That(gb.DebugReadByte(0x8010)).IsEqualTo((byte)0xFF);
    }

    // ---- What the retrofit ADDED (score text, sprite cursor) actually renders -----------------
    // Boots the real, unmodified ROM exactly like Sample_RomBootsIntoMainAndInitializes, then reads back
    // the two things Game.cs could not do before this slice: a text label + live number on the BG map
    // (Text.Draw/Text.DrawNumber), and a sprite tracking the active cell (Sprites). No input is sent, so
    // Board.Score stays 0 for the whole run — the digits are deterministic.

    [Test]
    public async Task Sample_RendersScoreLabelAndNumberToTheBackgroundMap()
    {
        var model = Compile([BoardSource, TilesSource, GameSource]);
        var rom = new LinkerType().Link([new LinkerInput("2048", model)]).RomData!;

        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Pc = 0x100;
        gb.Registers.Sp = 0xFFFE;
        for (int i = 0; i < 2_000_000; i++)
            gb.StepInstruction();

        // Text.Draw(1, 0, "SCORE") -> map row 0, cols 1..5 ($9801..$9805). Glyph = Tiles.FontFirstTile
        // (13) + (ch - 0x20): 'S'=64, 'C'=48, 'O'=60, 'R'=63, 'E'=50.
        await Assert.That(gb.DebugReadByte(0x9801)).IsEqualTo((byte)64); // S
        await Assert.That(gb.DebugReadByte(0x9802)).IsEqualTo((byte)48); // C
        await Assert.That(gb.DebugReadByte(0x9803)).IsEqualTo((byte)60); // O
        await Assert.That(gb.DebugReadByte(0x9804)).IsEqualTo((byte)63); // R
        await Assert.That(gb.DebugReadByte(0x9805)).IsEqualTo((byte)50); // E

        // Text.DrawNumber(7, 0, Board.Score, 5) with Score == 0 (no input sent) right-aligns "    0":
        // 4 space glyphs (13) at cols 7..10 ($9807..$980A), then the '0' glyph (13 + 16 = 29) at col 11
        // ($980B).
        await Assert.That(gb.DebugReadByte(0x9807)).IsEqualTo((byte)13);
        await Assert.That(gb.DebugReadByte(0x9808)).IsEqualTo((byte)13);
        await Assert.That(gb.DebugReadByte(0x9809)).IsEqualTo((byte)13);
        await Assert.That(gb.DebugReadByte(0x980A)).IsEqualTo((byte)13);
        await Assert.That(gb.DebugReadByte(0x980B)).IsEqualTo((byte)29);
    }

    [Test]
    public async Task Sample_CursorSpriteIsVisibleAndFlushedToOam()
    {
        var model = Compile([BoardSource, TilesSource, GameSource]);
        var rom = new LinkerType().Link([new LinkerInput("2048", model)]).RomData!;

        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Pc = 0x100;
        gb.Registers.Sp = 0xFFFE;
        for (int i = 0; i < 2_000_000; i++)
            gb.StepInstruction();

        // OAM slot 0 (Sprites.Get(0, ...)), Pan Docs order Y/X/Tile/Attr at $FE00..$FE03. The first
        // Video.EndFrame() flushes the shadow to real OAM via RunOamDma well within this step budget.
        // CursorRow/CursorCol come from the DIV-seeded random spawn, so X/Y aren't asserted exactly —
        // only that the DMA actually landed the cursor's own tile and that it isn't hidden (Y == 0 is
        // this library's one hide convention).
        await Assert.That(gb.DebugReadByte(0xFE02)).IsEqualTo((byte)12); // Tiles.CursorSpriteTile
        await Assert.That(gb.DebugReadByte(0xFE00)).IsNotEqualTo((byte)0); // Y != 0 -> not hidden
    }

    // ---- Its game logic runs correctly in the emulator (CIL-lowered) --------------------------

    [Test]
    public async Task SlideLine_MergesEqualPair() =>
        await Assert
            .That(
                Run(
                    TestEntry(
                        "Board.Reset(); Board.SetCell(0,1); Board.SetCell(1,1); "
                            + "Board.Slide(Direction.Left); return Board.GetCell(0);"
                    )
                )
            )
            .IsEqualTo((ushort)2);

    [Test]
    public async Task SlideLine_CompactsAcrossGap() =>
        await Assert
            .That(
                Run(
                    TestEntry(
                        "Board.Reset(); Board.SetCell(0,1); Board.SetCell(2,1); "
                            + "Board.Slide(Direction.Left); return Board.GetCell(0);"
                    )
                )
            )
            .IsEqualTo((ushort)2);

    [Test]
    public async Task MoveRight_MergesRowIntoRightEdge() =>
        await Assert
            .That(
                Run(
                    TestEntry(
                        "Board.Reset(); Board.SetCell(0,1); Board.SetCell(1,1); "
                            + "Board.Slide(Direction.Right); return Board.GetCell(3);"
                    )
                )
            )
            .IsEqualTo((ushort)2);

    [Test]
    public async Task MoveDown_MergesColumnIntoBottomEdge() =>
        await Assert
            .That(
                Run(
                    TestEntry(
                        "Board.Reset(); Board.SetCell(0,1); Board.SetCell(4,1); "
                            + "Board.Slide(Direction.Down); return Board.GetCell(12);"
                    )
                )
            )
            .IsEqualTo((ushort)2);

    [Test]
    public async Task Spawn_FillsExactlyOneEmptyCell()
    {
        var body = TestEntry(
            """
            Board.Reset();
            Board.SpawnTile();
            byte n = 0;
            for (byte i = 0; i < 16; i++) if (Board.GetCell(i) != 0) n++;
            return n;
            """
        );
        await Assert.That(Run(body)).IsEqualTo((ushort)1);
    }

    [Test]
    public async Task Spawn_ReturnsFalseWhenBoardFull()
    {
        var body = TestEntry(
            """
            Board.Reset();
            for (byte i = 0; i < 16; i++) Board.SetCell(i, 5);
            return (ushort)(Board.SpawnTile() ? 1 : 0);
            """
        );
        await Assert.That(Run(body)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task CanMove_FalseOnGridlock_TrueWithAnOpening()
    {
        var locked = TestEntry(
            """
            Board.Reset();
            for (byte i = 0; i < 16; i++) Board.SetCell(i, (byte)(i + 1));
            return (ushort)(Board.CanMove() ? 1 : 0);
            """
        );
        await Assert.That(Run(locked)).IsEqualTo((ushort)0);

        var opening = TestEntry(
            """
            Board.Reset();
            for (byte i = 0; i < 16; i++) Board.SetCell(i, (byte)(i + 1));
            Board.SetCell(5, 0);
            return (ushort)(Board.CanMove() ? 1 : 0);
            """
        );
        await Assert.That(Run(opening)).IsEqualTo((ushort)1);
    }

    [Test]
    public async Task HasWon_DetectsThe2048Tile()
    {
        await Assert
            .That(
                Run(
                    TestEntry(
                        "Board.Reset(); Board.SetCell(7,11); return (ushort)(Board.HasWon() ? 1 : 0);"
                    )
                )
            )
            .IsEqualTo((ushort)1);
        await Assert
            .That(
                Run(
                    TestEntry(
                        "Board.Reset(); Board.SetCell(7,10); return (ushort)(Board.HasWon() ? 1 : 0);"
                    )
                )
            )
            .IsEqualTo((ushort)0);
    }

    [Test]
    public async Task Render_PaintsBottomRowCellsToTheCorrectTilemapAddress()
    {
        // Same regression guard as Game2048Tests: the bottom-right board cell (r=3, c=3) maps to
        // tilemap (row 12, col 15) = $9800 + 399. Read back through VRAM with the LCD off.
        var body = TestEntry(
            """
            Hardware.LCDC = 0;
            Board.Reset();
            Board.SetCell(15, 9);
            Tiles.RenderBoard();
            return *(Gb.TileMap + 12 * 32 + 15);
            """
        );
        await Assert.That(Run(body)).IsEqualTo((ushort)9);
    }

    // ---- The optimized real ROM boots and behaves identically ---------------------------------

    [Test]
    public async Task Optimized_SampleRomBootsIntoMainAndInitializes()
    {
        var model = Compile([BoardSource, TilesSource, GameSource], optimize: true);
        var rom = new LinkerType().Link([new LinkerInput("2048", model)]).RomData!;

        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Pc = 0x100;
        gb.Registers.Sp = 0xFFFE;
        for (int i = 0; i < 2_000_000; i++)
            gb.StepInstruction();

        await Assert.That(gb.DebugReadByte(0x8010)).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Optimized_SlideLogicMatchesUnoptimized()
    {
        var body = TestEntry(
            "Board.Reset(); Board.SetCell(0,1); Board.SetCell(1,1); "
                + "Board.Slide(Direction.Left); return Board.GetCell(0);"
        );
        await Assert.That(Run(body, optimize: true)).IsEqualTo(Run(body, optimize: false));
        await Assert.That(Run(body, optimize: true)).IsEqualTo((ushort)2);
    }
}
