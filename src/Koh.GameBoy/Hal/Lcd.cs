namespace Koh.GameBoy;

/// <summary>The LCD controller: turn the display on/off and set the palette and scroll. A thin,
/// intent-named wrapper over the LCDC/BGP/SCX/SCY registers so games don't poke them directly.</summary>
public static class Lcd
{
    /// <summary>LCD off, so VRAM can be rewritten freely.</summary>
    public static void Off()
    {
        Hardware.LCDC = 0x00;
    }

    /// <summary>LCD on: background enabled, tile data at $8000, tile map at $9800.</summary>
    public static void On()
    {
        Hardware.LCDC = 0x91;
    }

    /// <summary>The background palette (two bits per shade, darkest first).</summary>
    public static void SetPalette(byte palette)
    {
        Hardware.BGP = palette;
    }

    /// <summary>Scroll the background to (x, y).</summary>
    public static void Scroll(byte x, byte y)
    {
        Hardware.SCX = x;
        Hardware.SCY = y;
    }
}
