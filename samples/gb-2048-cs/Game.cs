// 2048 for the Game Boy, written in Koh C#, built on the Koh.GameBoy.Graphics library
// (docs/superpowers/specs/2026-07-15-graphics-library-design.md §5, item 1: the first sample retrofit).
//
// This is an ordinary .NET project built by the Koh SDK. The same source compiles two ways:
//   * the Koh compiler lowers it to a bootable Game Boy ROM (2048.gb), which `dotnet run` opens in the
//     Koh emulator (the SDK's default run behavior);
//   * the plain .NET SDK also builds it into a managed binary that renders the board to the terminal
//     against the Koh.GameBoy reference runtime — run that binary directly (`dotnet exec`).
//
// The generic Game Boy surface — the low-level Hal primitives plus the higher-level Graphics library
// (Video, TileSet, Bg, Font, Text, Sprites, Palettes) — lives in the Koh.GameBoy framework. This project
// holds only what is specific to 2048: the Board rules, the Tiles art/layout, and this loop, which reads
// high-level and touches no bytes, registers, or addresses.
using Koh.GameBoy.Graphics;

namespace Koh.Samples.Gb2048CSharp;

static class Game
{
    static void Main()
    {
        Video.Init(); // LCD off; both tilemaps + OAM cleared; all sprites hidden; CGB detected

        Tiles.Load(); // board art at tile 0, cursor sprite art right after it
        Font.LoadDefault(Tiles.FontFirstTile);

        // Dual-authored: the CGB colors are used on CGB hardware, dmgShades (the same 0xE4 identity
        // ramp the original Lcd.SetPalette(0xE4) call used) on DMG — one call, both machines.
        Palettes.SetBg(0, Rgb.White, Rgb.Make(20, 25, 20), Rgb.Make(8, 14, 8), Rgb.Black, 0xE4);
        // The cursor sprite is drawn entirely in OBJ color 3 (see Tiles.CursorTile) — 0xC0 (11 00 00 00)
        // puts color 3 at the darkest DMG shade so the diamond reads as a solid dark marker against the
        // light board tiles; colors 1-2 are unused by this sprite, so their bits don't matter.
        Palettes.SetObj(0, 0, Rgb.White, Rgb.Make(31, 10, 10), Rgb.Black, 0xC0);

        Board.Reset();
        for (byte i = 0; i < 2; i++) // 2048 opens with two tiles on the board
            Board.SpawnTile();
        Tiles.RenderBoard();

        Text.Draw(1, 0, "SCORE");

        // The sprite cursor: previously impossible (no C# code touched Gb.Oam at all). Tracks the most
        // recently spawned cell (Board.CursorRow/CursorCol).
        Sprite cursor;
        Sprites.Get(0, out cursor);
        cursor.SetTile(Tiles.CursorSpriteTile);
        MoveCursor(cursor);

        Video.ShowSprites(SpriteSize.Size8x8);
        Video.Start();

        while (true)
        {
            byte pressed = Joypad.Pressed(); // rising edges, no hand-rolled previous-mask XOR

            // Slide toward the first direction newly pressed this frame (if any); same left/right/up/
            // down priority the original hand-rolled Direction-enum loop used.
            bool moved;
            if (Joypad.IsPressed(pressed, Button.Left))
                moved = Board.Slide(Direction.Left);
            else if (Joypad.IsPressed(pressed, Button.Right))
                moved = Board.Slide(Direction.Right);
            else if (Joypad.IsPressed(pressed, Button.Up))
                moved = Board.Slide(Direction.Up);
            else if (Joypad.IsPressed(pressed, Button.Down))
                moved = Board.Slide(Direction.Down);
            else
                moved = false;

            if (moved)
            {
                Board.SpawnTile();
                Tiles.RenderBoard(); // Bg.Fill is immediate-checked; no manual vblank wait needed
            }

            MoveCursor(cursor);
            Text.DrawNumber(7, 0, Board.Score, 5);

            Video.EndFrame(); // vblank + OAM flush (moves the cursor sprite), one call
        }
    }

    private static void MoveCursor(Sprite cursor) =>
        cursor.Move(Tiles.CellPixelX(Board.CursorCol), Tiles.CellPixelY(Board.CursorRow));
}
