static unsafe class Surface
{
    static byte[] pixels = new byte[256];

    internal static byte Width()
    {
        return 32;
    }

    internal static byte Height()
    {
        return 32;
    }

    internal static void Clear()
    {
        for (ushort i = 0; i < 256; i++)
            pixels[i] = 0;
    }

    internal static void SetPixel(byte x, byte y, byte color)
    {
        if (x >= 32 || y >= 32)
            return;
        ushort tile = (ushort)((ushort)(y >> 3) * 4 + (x >> 3));
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

    // Byte-granular span fill for FillTriangle's scanlines — same dither/coverage derivation as
    // double-buffered/Surface.cs's FillSpan (see there for the full comment); array-indexed here
    // instead of pointer-based, tilesPerRow = 4.
    internal static void FillSpan(byte y, byte xa, byte xb, byte color)
    {
        byte dither = color > 1 ? (byte)(0x88 >> (y & 3)) : (byte)0x00;
        byte fullBit0 = (color & 1) != 0 ? (byte)0xFF : (byte)0x00;
        byte plane0 = (byte)(fullBit0 ^ dither);
        byte bit1Dither = (color & 1) == 0 ? dither : (byte)0x00;
        byte fullBit1 = (color & 2) != 0 ? (byte)0xFF : (byte)0x00;
        byte plane1 = (byte)(fullBit1 ^ bit1Dither);

        byte firstByte = (byte)(xa >> 3);
        byte lastByte = (byte)(xb >> 3);
        ushort tile = (ushort)((ushort)(y >> 3) * 4 + firstByte);
        ushort o = (ushort)(tile * 16 + (y & 7) * 2);

        if (firstByte == lastByte)
        {
            byte cover = (byte)((byte)(0xFF >> (xa & 7)) & (byte)(0xFF << (7 - (xb & 7))));
            pixels[o] &= (byte)~cover;
            pixels[o] |= (byte)(plane0 & cover);
            pixels[o + 1] &= (byte)~cover;
            pixels[o + 1] |= (byte)(plane1 & cover);
            return;
        }

        byte coverFirst = (byte)(0xFF >> (xa & 7));
        pixels[o] &= (byte)~coverFirst;
        pixels[o] |= (byte)(plane0 & coverFirst);
        pixels[o + 1] &= (byte)~coverFirst;
        pixels[o + 1] |= (byte)(plane1 & coverFirst);

        for (byte b = (byte)(firstByte + 1); b < lastByte; b++)
        {
            o = (ushort)(o + 16);
            pixels[o] = plane0;
            pixels[o + 1] = plane1;
        }

        byte coverLast = (byte)(0xFF << (7 - (xb & 7)));
        o = (ushort)(o + 16);
        pixels[o] &= (byte)~coverLast;
        pixels[o] |= (byte)(plane0 & coverLast);
        pixels[o + 1] &= (byte)~coverLast;
        pixels[o + 1] |= (byte)(plane1 & coverLast);
    }

    internal static void Initialize()
    {
        Lcd.Off();
        // Tile data 0..15 holds the 4x4 content grid and 20 is the blank filler; 16..19 and 21..255
        // are spare and unowned — zero them so nothing but the blank tile can ever show through (the
        // DMG boot hand-off leaves the cartridge logo in tiles 1-24).
        for (ushort t = 16; t < 256; t++)
            TileData.Clear((byte)t);
        // Blank the full 32x32 map, not just the visible 20x18 window: Present() scrolls SCX by a few
        // pixels, which wraps columns >= 20 (and column 31) into view. Leaving those uninitialized let
        // them replicate whatever tile the leftover byte pointed at (tile 0's live content), which is
        // what produced the striped background.
        Tilemap.Clear(20);
        for (byte r = 0; r < 4; r++)
        for (byte c = 0; c < 4; c++)
            Tilemap.SetTile((byte)(8 + c), (byte)(7 + r), (byte)(r * 4 + c));
    }

    internal static void Present()
    {
        Lcd.Off();
        for (ushort i = 0; i < 256; i++)
            *(Gb.Vram + i) = pixels[i];
        Lcd.On();

        Ppu.WaitVBlank();
        for (byte skip = 0; skip < 55; skip++)
            Ppu.WaitForHBlank();
        for (byte line = 0; line < 32; line++)
        {
            Ppu.WaitForHBlank();
            Hardware.SCX = (byte)(FixedMath.Sin((byte)(line * 4)) / 32 + 2);
        }
        Ppu.WaitVBlank();
        Hardware.SCX = 0;
    }
}
