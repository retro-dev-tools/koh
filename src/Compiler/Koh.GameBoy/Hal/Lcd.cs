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

    /// <summary>Select which tile-data block the background uses (LCDC bit 4): <c>true</c> for
    /// $8000 addressing (indexes 0..127 read pixel data at $8000-$87FF), <c>false</c> for $8800
    /// addressing (the same indexes 0..127 read $9000-$97FF). Indexes 128..255 map to $8800-$8FFF
    /// in BOTH modes, so a tile there (e.g. a blank border tile) is shared. A single register write,
    /// which is what makes the classic double-buffer trick work: keep one static tilemap and flip
    /// the visible page with this during vblank instead of rewriting map cells.</summary>
    public static void SelectTileData(bool at8000)
    {
        if (at8000)
            Hardware.LCDC |= 0x10;
        else
            Hardware.LCDC &= 0xEF;
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
