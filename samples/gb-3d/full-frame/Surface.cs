static unsafe class Surface
{
    // 16 cols x 15 rows of content tiles (0..239) instead of a full 16x16 (0..255): giving up one tile
    // row of drawing area buys 16 spare tile-data slots (240..255) that are never part of the rotating
    // content, so they can hold a genuinely static blank tile instead of a byte that's overwritten (and
    // would otherwise flash live cube pixels) every frame.
    const byte SpareTileStart = 240;
    const byte BlankTile = 255;

    static byte[] pixels = new byte[3840];

    internal static byte Width()
    {
        return 128;
    }

    internal static byte Height()
    {
        return 120;
    }

    internal static void Clear()
    {
        for (ushort i = 0; i < 3840; i++)
            pixels[i] = 0;
    }

    internal static void SetPixel(byte x, byte y, byte color)
    {
        if (x >= 128 || y >= 120)
            return;
        ushort tile = (ushort)((ushort)(y >> 3) * 16 + (x >> 3));
        ushort o = (ushort)(tile * 16 + (y & 7) * 2);
        byte m = (byte)(0x80 >> (x & 7));
        if ((color & 1) != 0)
            pixels[o] |= m;
        else
            pixels[o] &= (byte)~m;
        if ((color & 2) != 0)
            pixels[o + 1] |= m;
        else
            pixels[o + 1] &= (byte)~m;
    }

    internal static void Initialize()
    {
        Lcd.Off();
        Tilemap.Clear(BlankTile);
        for (byte t = SpareTileStart; t != 0; t++)
            TileData.Clear(t);
        for (byte r = 0; r < 15; r++)
        for (byte c = 0; c < 16; c++)
            Tilemap.SetTile((byte)(2 + c), (byte)(1 + r), (byte)(r * 16 + c));
    }

    internal static void Present()
    {
        // Pace to the hardware frame rate and align the LCD-off tile upload with the start of vblank
        // (as the other demos do via Ppu.WaitVBlank()), so the whole-frame rewrite lands in the blanked
        // window instead of at an arbitrary point relative to the previous frame's display time. Without
        // this the loop is uncapped: rotation speed tracks raw CPU speed (and doubles again under CGB
        // double-speed) and the LCD-off/on cycle straddles frames unpredictably. Turning the LCD off for
        // the whole copy (not just an edge-synchronized burst) sidesteps the mode-3 VRAM lock entirely —
        // simplest-safe over edge-sync since this demo already accepted the LCD-off flash tradeoff.
        Ppu.WaitVBlank();
        Lcd.Off();
        for (ushort i = 0; i < 3840; i++)
            *(Gb.Vram + i) = pixels[i];
        Lcd.On();
    }
}
