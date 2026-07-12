static unsafe class Surface
{
    static byte[] pixels = new byte[1920];
    static byte page;

    internal static byte Width()
    {
        return 96;
    }

    internal static byte Height()
    {
        return 80;
    }

    internal static void Clear()
    {
        for (ushort i = 0; i < 1920; i++)
            pixels[i] = 0;
    }

    internal static void SetPixel(byte x, byte y, byte color)
    {
        if (x >= 96 || y >= 80)
            return;
        ushort tile = (ushort)((ushort)(y >> 3) * 12 + (x >> 3));
        ushort offset = (ushort)(tile * 16 + (y & 7) * 2);
        byte mask = (byte)(0x80 >> (x & 7));
        if ((color & 1) != 0)
            pixels[offset] |= mask;
        else
            pixels[offset] &= (byte)~mask;
        if ((color & 2) != 0)
            pixels[offset + 1] |= mask;
        else
            pixels[offset + 1] &= (byte)~mask;
    }

    internal static void Initialize()
    {
        Lcd.Off();
        for (byte r = 0; r < 10; r++)
        for (byte c = 0; c < 12; c++)
            Tilemap.SetTile((byte)(4 + c), (byte)(4 + r), (byte)(r * 12 + c));
        page = 1;
    }

    internal static void Present()
    {
        byte firstTile = page == 0 ? (byte)0 : (byte)120;
        ushort baseOffset = (ushort)((ushort)firstTile * 16);
        for (ushort i = 0; i < 1920; i++)
        {
            Ppu.WaitForVramAccess();
            *(Gb.Vram + baseOffset + i) = pixels[i];
        }
        Ppu.WaitVBlank();
        for (byte r = 0; r < 10; r++)
        for (byte c = 0; c < 12; c++)
            Tilemap.SetTile((byte)(4 + c), (byte)(4 + r), (byte)(firstTile + r * 12 + c));
        page ^= 1;
        Benchmark.CompleteFrame();
    }
}
