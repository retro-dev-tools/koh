using System.Text;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends.CSharp;
using Koh.Compiler.Ir;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Samples;

/// <summary>
/// Compiles the <c>samples/gb-2048-cs</c> game (Board / Video / Lcd / Joypad / Game) through the real
/// pipeline (Koh C# frontend -> IR -> SM83 backend -> linker -> ROM) and runs its game logic in the
/// emulator through the public <c>Board</c> API. The end-to-end proof that a non-trivial, multi-file
/// Game Boy program written as ordinary static classes builds and behaves correctly.
/// </summary>
public class Game2048Tests
{
    /// <summary>Every sample source, concatenated as one translation unit (as the SDK compiles it).</summary>
    private static readonly string GameSource = ReadSample(includeGame: true);

    /// <summary>The sample minus its Game.Main, so a test can supply its own Main as the ROM entry.</summary>
    private static readonly string GameLibrary = ReadSample(includeGame: false);

    private static string ReadSample(bool includeGame)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("could not locate the repository root (Koh.slnx).");
        var sampleDir = Path.Combine(dir.FullName, "samples", "gb-2048-cs");

        var sb = new StringBuilder();
        foreach (
            var file in Directory
                .GetFiles(sampleDir, "*.cs")
                .OrderBy(f => f, StringComparer.Ordinal)
        )
        {
            if (!includeGame && Path.GetFileName(file) == "Game.cs")
                continue;
            sb.Append(File.ReadAllText(file)).Append('\n');
        }
        return sb.ToString();
    }

    // ---- The real sample compiles to a valid, bootable ROM -----------------

    [Test]
    public async Task Sample_CompilesWithoutDiagnostics()
    {
        var diagnostics = new DiagnosticBag();
        var module = new CSharpFrontend().Lower(
            SourceText.From(GameSource, "2048.cs"),
            diagnostics
        );
        new Sm83Backend().Compile(module, diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
    }

    [Test]
    public async Task Sample_ProducesVerifiableIr()
    {
        var module = new CSharpFrontend().Lower(
            SourceText.From(GameSource, "2048.cs"),
            new DiagnosticBag()
        );
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task Sample_LinksToBootableRom()
    {
        var model = new Sm83Backend().Compile(
            new CSharpFrontend().Lower(SourceText.From(GameSource, "2048.cs"), new DiagnosticBag()),
            new DiagnosticBag()
        );
        var rom = new LinkerType().Link([new LinkerInput("2048", model)]).RomData!;

        // A DMG cartridge boots through 0x0100 (nop; jp entry) and the boot ROM verifies the logo.
        await Assert.That(rom[0x100]).IsEqualTo((byte)0x00); // NOP
        await Assert.That(rom[0x101]).IsEqualTo((byte)0xC3); // JP a16
        await Assert.That(rom[0x104]).IsEqualTo((byte)0xCE); // first byte of the Nintendo logo
        await Assert.That(rom[0x105]).IsEqualTo((byte)0xED);
    }

    // ---- Its game logic runs correctly in the emulator ---------------------

    /// <summary>
    /// Compile a test whose Main (placed first, so it lands at the ROM entry) drives the sample's
    /// Board/Video API, run it, and return HL.
    /// </summary>
    private static ushort Run(string testMain)
    {
        var src = testMain + "\n" + GameLibrary;
        var module = new CSharpFrontend().Lower(
            SourceText.From(src, "game.cs"),
            new DiagnosticBag()
        );
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var rom = new LinkerType().Link([new LinkerInput("t", model)]).RomData!;

        int start = Sm83Backend.CodeBase;
        int length = model.Sections[0].Data.Length;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start; // the test Main is emitted first -> entry is at CodeBase
        for (int steps = 0; steps < 1_000_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= start + length)
                break;
            gb.StepInstruction();
        }
        return gb.Registers.HL;
    }

    // Set a row's four cells, slide the whole board Left (empty rows are unaffected), read one cell.
    private static ushort SlideRow(string setup, int index) =>
        Run(
            $"static ushort Main() {{ Board.Reset(); {setup} Board.Slide(Direction.Left); return Board.Get((byte){index}); }}"
        );

    private static async Task AssertRow(string setup, byte[] expected)
    {
        for (int i = 0; i < 4; i++)
            await Assert.That(SlideRow(setup, i)).IsEqualTo((ushort)expected[i]);
    }

    [Test]
    public async Task SlideLine_MergesEqualPair() =>
        await AssertRow("Board.Set(0,1); Board.Set(1,1);", [2, 0, 0, 0]); // 2 2 . .  ->  4 . . .

    [Test]
    public async Task SlideLine_CompactsAcrossGap() =>
        await AssertRow("Board.Set(0,1); Board.Set(2,1);", [2, 0, 0, 0]); // 2 . 2 .  ->  4 . . .

    [Test]
    public async Task SlideLine_MergesTwoPairsNotTriple() =>
        await AssertRow(
            "Board.Set(0,1); Board.Set(1,1); Board.Set(2,1); Board.Set(3,1);",
            [2, 2, 0, 0]
        );

    [Test]
    public async Task SlideLine_MergesLeftmostPairOfTriple() =>
        await AssertRow("Board.Set(0,1); Board.Set(1,1); Board.Set(2,1);", [2, 1, 0, 0]);

    [Test]
    public async Task SlideLine_SlidesLoneTileToEdge() =>
        await AssertRow("Board.Set(3,1);", [1, 0, 0, 0]); // . . . 2  ->  2 . . .

    // Each direction merges a pair into the right edge cell.
    private static ushort Move(string setup, string dir, int index) =>
        Run(
            $"static ushort Main() {{ Board.Reset(); {setup} Board.Slide(Direction.{dir}); return Board.Get((byte){index}); }}"
        );

    [Test]
    public async Task MoveLeft_MergesRowIntoLeftEdge() =>
        await Assert.That(Move("Board.Set(0,1); Board.Set(1,1);", "Left", 0)).IsEqualTo((ushort)2);

    [Test]
    public async Task MoveRight_MergesRowIntoRightEdge() =>
        await Assert.That(Move("Board.Set(0,1); Board.Set(1,1);", "Right", 3)).IsEqualTo((ushort)2);

    [Test]
    public async Task MoveUp_MergesColumnIntoTopEdge() =>
        await Assert.That(Move("Board.Set(0,1); Board.Set(4,1);", "Up", 0)).IsEqualTo((ushort)2);

    [Test]
    public async Task MoveDown_MergesColumnIntoBottomEdge() =>
        await Assert.That(Move("Board.Set(0,1); Board.Set(4,1);", "Down", 12)).IsEqualTo((ushort)2);

    [Test]
    public async Task Slide_ReportsWhetherAnythingChanged()
    {
        await Assert
            .That(
                Run(
                    "static ushort Main(){ Board.Reset(); Board.Set(1,1); return (ushort)(Board.Slide(Direction.Left) ? 1 : 0); }"
                )
            )
            .IsEqualTo((ushort)1);
        await Assert
            .That(
                Run(
                    "static ushort Main(){ Board.Reset(); Board.Set(0,1); return (ushort)(Board.Slide(Direction.Left) ? 1 : 0); }"
                )
            )
            .IsEqualTo((ushort)0);
    }

    [Test]
    public async Task Spawn_FillsExactlyOneEmptyCell()
    {
        const string src =
            @"static ushort Main() {
            Board.Reset();
            Board.Spawn();
            byte n = 0;
            for (byte i = 0; i < 16; i++) if (Board.Get(i) != 0) n++;
            return n;
        }";
        await Assert.That(Run(src)).IsEqualTo((ushort)1);
    }

    [Test]
    public async Task Spawn_ReturnsFalseWhenBoardFull()
    {
        const string src =
            @"static ushort Main() {
            Board.Reset();
            for (byte i = 0; i < 16; i++) Board.Set(i, 5);
            return (ushort)(Board.Spawn() ? 1 : 0);
        }";
        await Assert.That(Run(src)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task CanMove_FalseOnGridlock_TrueWithAnOpening()
    {
        // A board of all-distinct ascending values has no merges and no gaps -> gridlocked.
        const string locked =
            @"static ushort Main() {
            Board.Reset();
            for (byte i = 0; i < 16; i++) Board.Set(i, (byte)(i + 1));
            return (ushort)(Board.CanMove() ? 1 : 0);
        }";
        await Assert.That(Run(locked)).IsEqualTo((ushort)0);

        const string opening =
            @"static ushort Main() {
            Board.Reset();
            for (byte i = 0; i < 16; i++) Board.Set(i, (byte)(i + 1));
            Board.Set(5, 0);
            return (ushort)(Board.CanMove() ? 1 : 0);
        }";
        await Assert.That(Run(opening)).IsEqualTo((ushort)1);
    }

    [Test]
    public async Task HasWon_DetectsThe2048Tile()
    {
        await Assert
            .That(
                Run(
                    "static ushort Main(){ Board.Reset(); Board.Set(7,11); return (ushort)(Board.HasWon() ? 1 : 0); }"
                )
            )
            .IsEqualTo((ushort)1);
        await Assert
            .That(
                Run(
                    "static ushort Main(){ Board.Reset(); Board.Set(7,10); return (ushort)(Board.HasWon() ? 1 : 0); }"
                )
            )
            .IsEqualTo((ushort)0);
    }

    [Test]
    public async Task Render_PaintsBottomRowCellsToTheCorrectTilemapAddress()
    {
        // The bottom-right board cell (r=3, c=3) maps to tilemap (row 12, col 15) = $9800 + 399.
        // Regression: the offset row*32 used to be computed in 8-bit and truncate (399 -> 143), so
        // the lower board rows scribbled over the wrong cells. Read the tile back through VRAM with
        // the LCD off, so the PPU is not blocking VRAM access.
        const string src =
            @"static ushort Main() {
            Hardware.LCDC = 0;
            Board.Reset();
            Board.Set(15, 9);
            Video.Render();
            return *(Gb.TileMap + 12 * 32 + 15);
        }";
        await Assert.That(Run(src)).IsEqualTo((ushort)9);
    }
}
