using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends.CSharp;
using Koh.Compiler.Ir;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Samples;

/// <summary>
/// Compiles the <c>samples/gb-2048-cs/2048.cs</c> game through the real pipeline (Koh C# frontend
/// -> IR -> SM83 backend -> linker -> ROM) and runs its actual game-logic functions in the emulator.
/// This is the end-to-end proof that a non-trivial Game Boy program builds and behaves correctly.
/// </summary>
public class Game2048Tests
{
    /// <summary>The game source, split into its reusable "library" (everything above Main) and Main.</summary>
    private static readonly string GameSource = File.ReadAllText(FindSample());
    private static readonly string GameLibrary = GameSource[
        ..GameSource.IndexOf("// ---- Entry point", StringComparison.Ordinal)
    ];

    private static string FindSample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("could not locate the repository root (Koh.slnx).");
        return Path.Combine(dir.FullName, "samples", "gb-2048-cs", "2048.cs");
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

    // ---- Its game-logic functions run correctly in the emulator ------------

    /// <summary>
    /// Build a program whose Main (placed first, so it lands at the ROM entry) sets up a board and
    /// exercises the sample's real functions, then compile it, run it, and return HL.
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
        gb.Registers.Pc = (ushort)start; // Main is emitted first -> entry is at CodeBase
        for (int steps = 0; steps < 1_000_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= start + length)
                break;
            gb.StepInstruction();
        }
        return gb.Registers.HL;
    }

    /// <summary>Slide a 4-cell line with SlideLine and read back one cell.</summary>
    private static ushort SlideCell(string cells, int index) =>
        Run(
            $"static ushort Main() {{ byte[] a = new byte[4]; {cells} SlideLine(&a[0]); return a[{index}]; }}"
        );

    private static async Task AssertLine(string cells, byte[] expected)
    {
        for (int i = 0; i < 4; i++)
            await Assert.That(SlideCell(cells, i)).IsEqualTo((ushort)expected[i]);
    }

    [Test]
    public async Task SlideLine_MergesEqualPair() =>
        await AssertLine("a[0]=1;a[1]=1;", [2, 0, 0, 0]); // 2 2 . .  ->  4 . . .

    [Test]
    public async Task SlideLine_CompactsAcrossGap() =>
        await AssertLine("a[0]=1;a[2]=1;", [2, 0, 0, 0]); // 2 . 2 .  ->  4 . . .

    [Test]
    public async Task SlideLine_MergesTwoPairsNotTriple() =>
        await AssertLine("a[0]=1;a[1]=1;a[2]=1;a[3]=1;", [2, 2, 0, 0]); // 2 2 2 2 -> 4 4 . .

    [Test]
    public async Task SlideLine_MergesLeftmostPairOfTriple() =>
        await AssertLine("a[0]=1;a[1]=1;a[2]=1;", [2, 1, 0, 0]); // 2 2 2 .  ->  4 2 . .

    [Test]
    public async Task SlideLine_SlidesLoneTileToEdge() => await AssertLine("a[3]=1;", [1, 0, 0, 0]); // . . . 2  ->  2 . . .

    // Move helpers pack a board index; verify each of the four directions merges into the right cell.
    private static ushort Move(string cells, byte dir, int index) =>
        Run(
            $"static ushort Main() {{ byte[] b = new byte[16]; {cells} MoveDir(&b[0], {dir}); return b[{index}]; }}"
        );

    [Test]
    public async Task MoveLeft_MergesRowIntoLeftEdge() =>
        await Assert.That(Move("b[0]=1;b[1]=1;", 0, 0)).IsEqualTo((ushort)2);

    [Test]
    public async Task MoveRight_MergesRowIntoRightEdge() =>
        await Assert.That(Move("b[0]=1;b[1]=1;", 1, 3)).IsEqualTo((ushort)2);

    [Test]
    public async Task MoveUp_MergesColumnIntoTopEdge() =>
        await Assert.That(Move("b[0]=1;b[4]=1;", 2, 0)).IsEqualTo((ushort)2);

    [Test]
    public async Task MoveDown_MergesColumnIntoBottomEdge() =>
        await Assert.That(Move("b[0]=1;b[4]=1;", 3, 12)).IsEqualTo((ushort)2);

    [Test]
    public async Task MoveDir_ReportsWhetherAnythingChanged()
    {
        await Assert
            .That(
                Run(
                    "static ushort Main(){ byte[] b=new byte[16]; b[1]=1; return MoveDir(&b[0],0); }"
                )
            )
            .IsEqualTo((ushort)1);
        await Assert
            .That(
                Run(
                    "static ushort Main(){ byte[] b=new byte[16]; b[0]=1; return MoveDir(&b[0],0); }"
                )
            )
            .IsEqualTo((ushort)0);
    }

    [Test]
    public async Task SpawnTile_FillsExactlyOneEmptyCell()
    {
        const string src =
            @"static ushort Main() {
            byte[] b = new byte[16];
            SpawnTile(&b[0], 3);
            byte n = 0;
            for (byte i = 0; i < 16; i++) if (b[i] != 0) n++;
            return n;
        }";
        await Assert.That(Run(src)).IsEqualTo((ushort)1);
    }

    [Test]
    public async Task SpawnTile_ReturnsZeroWhenBoardFull()
    {
        const string src =
            @"static ushort Main() {
            byte[] b = new byte[16];
            for (byte i = 0; i < 16; i++) b[i] = 5;
            return SpawnTile(&b[0], 3);
        }";
        await Assert.That(Run(src)).IsEqualTo((ushort)0);
    }

    [Test]
    public async Task CanMove_FalseOnGridlock_TrueWithAnOpening()
    {
        // A board of all-distinct ascending values has no merges and no gaps -> gridlocked.
        const string locked =
            @"static ushort Main() {
            byte[] b = new byte[16];
            for (byte i = 0; i < 16; i++) b[i] = (byte)(i + 1);
            return CanMove(&b[0]);
        }";
        await Assert.That(Run(locked)).IsEqualTo((ushort)0);

        const string opening =
            @"static ushort Main() {
            byte[] b = new byte[16];
            for (byte i = 0; i < 16; i++) b[i] = (byte)(i + 1);
            b[5] = 0;
            return CanMove(&b[0]);
        }";
        await Assert.That(Run(opening)).IsEqualTo((ushort)1);
    }

    [Test]
    public async Task HasWon_DetectsThe2048Tile()
    {
        await Assert
            .That(
                Run("static ushort Main(){ byte[] b=new byte[16]; b[7]=11; return HasWon(&b[0]); }")
            )
            .IsEqualTo((ushort)1);
        await Assert
            .That(
                Run("static ushort Main(){ byte[] b=new byte[16]; b[7]=10; return HasWon(&b[0]); }")
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
            byte[] b = new byte[16];
            b[15] = 9;
            Render(&b[0]);
            byte* map = (byte*)0x9800;
            return *(map + 12 * 32 + 15);
        }";
        await Assert.That(Run(src)).IsEqualTo((ushort)9);
    }
}
