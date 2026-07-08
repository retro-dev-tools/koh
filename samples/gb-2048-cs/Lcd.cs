// Display hardware abstraction: the handful of LCD-control registers the game touches, named by
// intent instead of by raw register writes.

static class Lcd
{
    // Turn the LCD off (so VRAM can be written freely) / on (background enabled, tile data at $8000,
    // tile map at $9800).
    internal static void Off()
    {
        Hardware.LCDC = 0x00;
    }

    internal static void On()
    {
        Hardware.LCDC = 0x91;
    }

    // The background palette (two bits per shade, darkest first).
    internal static void SetPalette(byte palette)
    {
        Hardware.BGP = palette;
    }

    // Scroll the background to (x, y).
    internal static void Scroll(byte x, byte y)
    {
        Hardware.SCX = x;
        Hardware.SCY = y;
    }
}
