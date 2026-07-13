static unsafe class Surface
{
    static byte[] pixels = new byte[4096];

    internal static byte Width()
    {
        return 128;
    }

    internal static byte Height()
    {
        return 128;
    }

    internal static void Clear()
    {
        for (ushort i = 0; i < 4096; i++)
            pixels[i] = 0;
    }

    internal static void SetPixel(byte x, byte y, byte color)
    {
        if (x >= 128 || y >= 128)
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
        for (byte r = 0; r < 16; r++)
        for (byte c = 0; c < 16; c++)
            Tilemap.SetTile((byte)(2 + c), (byte)(1 + r), (byte)(r * 16 + c));
    }

    internal static void Present()
    {
        // Pace to the hardware frame rate and align the LCD-off tile upload with the start of vblank
        // (as the other demos do via Ppu.WaitVBlank()), so the whole-frame rewrite lands in the blanked
        // window instead of at an arbitrary point relative to the previous frame's display time. Without
        // this the loop is uncapped: rotation speed tracks raw CPU speed (and doubles again under CGB
        // double-speed) and the LCD-off/on cycle straddles frames unpredictably.
        Ppu.WaitVBlank();
        Lcd.Off();
        for (ushort i = 0; i < 4096; i++)
            *(Gb.Vram + i) = pixels[i];
        Lcd.On();
    }
}
