namespace Koh.GameBoy;

/// <summary>The LCD controller: turn the display on/off and set the palette and scroll. A thin,
/// intent-named wrapper over the LCDC/BGP/SCX/SCY registers so games don't poke them directly.</summary>
public static class Lcd
{
    /// <summary>LCD off, so VRAM can be rewritten freely. Disabling the LCD outside vertical blank is
    /// a documented hardware hazard (Pan Docs "LCD Timing"): the PPU's mode/scanline state is only
    /// guaranteed sane if the display is stopped during vblank. Game code calls this right after boot
    /// handoff, when the LCD is on and mid-frame, so wait for vblank first — but only if the LCD is
    /// actually on and not already there: waiting while the LCD is off would spin forever (LY never
    /// advances with no PPU clock), and waiting while already in vblank would waste a whole frame.</summary>
    public static void Off()
    {
        if ((Hardware.LCDC & 0x80) != 0 && Hardware.LY < 144)
            Ppu.WaitVBlank();
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
