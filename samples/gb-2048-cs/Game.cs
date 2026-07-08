// 2048 for the Game Boy, written in Koh C#.
//
// This is an ordinary .NET project built by the Koh SDK. The same source compiles two ways:
//   * the Koh compiler lowers it to a bootable Game Boy ROM (2048.gb);
//   * the plain .NET SDK runs it on the desktop against the Koh.GameBoy reference runtime
//     (`dotnet run`), which renders the board to the terminal.
//
// The game is split by responsibility: Board (rules + state), Video (a small drawing library),
// Lcd and Joypad (display and input HALs). This file is just the loop that ties them together —
// no raw bytes, registers, or video addresses in sight.

static class Game
{
    static void Main()
    {
        Lcd.Off(); // LCD off so we can build the tileset in VRAM
        Lcd.SetPalette(0xE4); // 11 10 01 00: dark -> light
        Lcd.Scroll(0, 0);

        Video.GenerateTiles();

        Board.Reset();
        Board.Spawn();
        Board.Spawn();
        Video.Render();

        Lcd.On();

        byte previous = 0;
        while (true)
        {
            byte held = Joypad.Read();
            byte pressed = (byte)((byte)(held ^ previous) & held); // rising edges only
            previous = held;

            bool moved = false;
            if (Joypad.Held(pressed, Direction.Left))
                moved = Board.Slide(Direction.Left);
            else if (Joypad.Held(pressed, Direction.Right))
                moved = Board.Slide(Direction.Right);
            else if (Joypad.Held(pressed, Direction.Up))
                moved = Board.Slide(Direction.Up);
            else if (Joypad.Held(pressed, Direction.Down))
                moved = Board.Slide(Direction.Down);

            if (moved)
            {
                Board.Spawn();
                Video.WaitVBlank();
                Video.Render();
            }
        }
    }
}
