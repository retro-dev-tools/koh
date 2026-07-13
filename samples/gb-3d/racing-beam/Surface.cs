static unsafe class Surface
{
    // 64 content tiles (0..63, an 8x8 grid) plus one blank filler tile (64); tiles 65..255 are spare
    // and unowned. The DMG boot hand-off leaves the cartridge logo in tiles 1-24, so every tile from
    // the blank filler up must be zeroed at Initialize — nothing but the blank tile can ever show
    // through.
    const byte BlankTile = 64;

    // The 1024-byte render target (64x64 px, 2bpp: 64*64/8*2 = 1024). Allocated from the WRAM arena
    // and 16-byte aligned, mirroring double-buffered/Surface.cs's Initialize pattern — Mem.Copy has
    // no alignment requirement of its own, but keeping every Surface's pixel buffer aligned the same
    // way keeps this file consistent with the other two demos.
    static byte* pixels;

    internal static byte Width()
    {
        return 64;
    }

    internal static byte Height()
    {
        return 64;
    }

    internal static void Clear()
    {
        Mem.Fill(pixels, 0, 1024);
    }

    internal static void SetPixel(byte x, byte y, byte color)
    {
        if (x >= 64 || y >= 64)
            return;
        ushort tile = (ushort)((ushort)(y >> 3) * 8 + (x >> 3));
        ushort offset = (ushort)(tile * 16 + (y & 7) * 2);
        byte mask = (byte)(0x80 >> (x & 7));
        if ((color & 1) != 0)
            *(pixels + offset) |= mask;
        else
            *(pixels + offset) &= (byte)~mask;
        if ((color & 2) != 0)
            *(pixels + offset + 1) |= mask;
        else
            *(pixels + offset + 1) &= (byte)~mask;
    }

    // Byte-granular span fill for FillTriangle's scanlines — same dither/coverage derivation as
    // double-buffered/Surface.cs's FillSpan (see there for the full comment); pointer-based,
    // tilesPerRow = 8.
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
        ushort tile = (ushort)((ushort)(y >> 3) * 8 + firstByte);
        ushort o = (ushort)(tile * 16 + (y & 7) * 2);

        if (firstByte == lastByte)
        {
            byte cover = (byte)((byte)(0xFF >> (xa & 7)) & (byte)(0xFF << (7 - (xb & 7))));
            *(pixels + o) &= (byte)~cover;
            *(pixels + o) |= (byte)(plane0 & cover);
            *(pixels + o + 1) &= (byte)~cover;
            *(pixels + o + 1) |= (byte)(plane1 & cover);
            return;
        }

        byte coverFirst = (byte)(0xFF >> (xa & 7));
        *(pixels + o) &= (byte)~coverFirst;
        *(pixels + o) |= (byte)(plane0 & coverFirst);
        *(pixels + o + 1) &= (byte)~coverFirst;
        *(pixels + o + 1) |= (byte)(plane1 & coverFirst);

        for (byte b = (byte)(firstByte + 1); b < lastByte; b++)
        {
            o = (ushort)(o + 16);
            *(pixels + o) = plane0;
            *(pixels + o + 1) = plane1;
        }

        byte coverLast = (byte)(0xFF << (7 - (xb & 7)));
        o = (ushort)(o + 16);
        *(pixels + o) &= (byte)~coverLast;
        *(pixels + o) |= (byte)(plane0 & coverLast);
        *(pixels + o + 1) &= (byte)~coverLast;
        *(pixels + o + 1) |= (byte)(plane1 & coverLast);
    }

    internal static void Initialize()
    {
        pixels = Mem.Alloc(1024 + 15);
        pixels = (byte*)(((ulong)pixels + 15) & ~15UL);
        Lcd.Off();
        // Tile data 0..63 holds the 8x8 content grid and 64 is the blank filler; 65..255 are spare
        // and unowned. Clear from the blank filler through 255 (byte wraps 255 -> 0, ending the loop)
        // — nothing but the blank tile can ever show through the boot hand-off's leftover logo tiles.
        for (byte t = BlankTile; t != 0; t++)
            TileData.Clear(t);
        // Blank the full 32x32 map, not just the visible 20x18 window: Present() scrolls SCX by a few
        // pixels, which wraps columns >= 20 (and column 31) into view. Leaving those uninitialized let
        // them replicate whatever tile the leftover byte pointed at (tile 0's live content), which is
        // what produced the striped background.
        Tilemap.Clear(BlankTile);
        for (byte r = 0; r < 8; r++)
        for (byte c = 0; c < 8; c++)
            Tilemap.SetTile((byte)(6 + c), (byte)(5 + r), (byte)(r * 8 + c));
    }

    internal static void Present()
    {
        Lcd.Off();
        Mem.Copy(Gb.Vram, pixels, 1024);
        Lcd.On();

        // The 8x8 tile block sits at tile rows 5..12 (Initialize), i.e. pixel rows 40..103 (64 px).
        // WaitForHBlank() lands on the Nth hblank-entry after WaitVBlank() at LY = N-1 (the first call
        // after vblank clears the rest of vblank plus line 0's OAM/transfer, landing at LY=0's
        // hblank). Skipping 39 hblanks lands at LY=38; the wobble loop's first iteration is the 40th
        // call (LY=39, affecting line 40's render) and its last (line=63) is the 103rd call (LY=102,
        // affecting line 103) — exactly the content's 40..103 pixel-row span. (Was skip=55/32 lines
        // for the old 32x32 viewport's rows 56..87; skip = contentStartRow - 1 in both cases.)
        Ppu.WaitVBlank();
        for (byte skip = 0; skip < 39; skip++)
            Ppu.WaitForHBlank();
        for (byte line = 0; line < 64; line++)
        {
            Ppu.WaitForHBlank();
            Hardware.SCX = (byte)(FixedMath.Sin((byte)(line * 4)) / 32 + 2);
        }
        Ppu.WaitVBlank();
        Hardware.SCX = 0;
    }
}
