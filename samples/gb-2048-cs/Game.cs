// 2048 for the Game Boy, written in Koh C#.
//
// This is an ordinary .NET project built by the Koh SDK. The same source compiles two ways:
//   * the Koh compiler lowers it to a bootable Game Boy ROM (2048.gb);
//   * the plain .NET SDK runs it on the desktop against the Koh.GameBoy reference runtime
//     (`dotnet run`), which renders the board to the terminal.
//
// The generic Game Boy surface — Lcd, Joypad, Tilemap/TileData, Ppu, Direction — lives in the
// Koh.GameBoy framework. This project holds only what is specific to 2048: the Board rules, the
// Tiles art, and this loop, which reads high-level and touches no bytes, registers, or addresses.

static class Game
{
    static void Main()
    {
        Lcd.Off(); // LCD off so we can build the tileset in VRAM
        Lcd.SetPalette(0xE4); // 11 10 01 00: dark -> light
        Lcd.Scroll(0, 0);

        Tiles.GenerateTileset();

        Board.Reset();
        for (byte i = 0; i < 2; i++) // 2048 opens with two tiles on the board
            Board.SpawnTile();
        Tiles.RenderBoard();

        Lcd.On();

        byte previous = 0;
        while (true)
        {
            byte held = Joypad.Read();
            byte pressed = (byte)((byte)(held ^ previous) & held); // rising edges only
            previous = held;

            // Slide toward the first direction newly pressed this frame (if any).
            bool moved = false;
            for (byte d = 0; d < 4; d++)
            {
                Direction dir = (Direction)d;
                if (Joypad.Held(pressed, dir))
                {
                    moved = Board.Slide(dir);
                    break;
                }
            }

            if (moved)
            {
                Board.SpawnTile();
                Ppu.WaitVBlank();
                Tiles.RenderBoard();
            }
        }
    }
}
