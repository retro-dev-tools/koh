static unsafe class Surface
{
    // 16 cols x 15 rows of content tiles (0..239) instead of a full 16x16 (0..255): giving up one tile
    // row of drawing area buys 16 spare tile-data slots (240..255) that are never part of the rotating
    // content, so they can hold a genuinely static blank tile instead of a byte that's overwritten (and
    // would otherwise flash live cube pixels) every frame.
    const byte SpareTileStart = 240;
    const byte BlankTile = 255;

    // The 3840-byte render target. Allocated from the WRAM arena (not a static array) and 16-byte
    // aligned, mirroring double-buffered/Surface.cs's Initialize pattern: the CGB path hands this
    // address to Cgb.CopyToVram, whose HDMA1/2 source registers ignore the low 4 bits, so a real,
    // aligned address is required (a managed array's address can't be taken in legal C#, and this
    // source also has to build as the desktop reference binary).
    static byte* pixels;

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
        Mem.Fill(pixels, 0, 3840);
    }

    internal static void SetPixel(byte x, byte y, byte color)
    {
        if (x >= 128 || y >= 120)
            return;
        ushort tile = (ushort)((ushort)(y >> 3) * 16 + (x >> 3));
        ushort o = (ushort)(tile * 16 + (y & 7) * 2);
        byte m = (byte)(0x80 >> (x & 7));
        if ((color & 1) != 0)
            *(pixels + o) |= m;
        else
            *(pixels + o) &= (byte)~m;
        if ((color & 2) != 0)
            *(pixels + o + 1) |= m;
        else
            *(pixels + o + 1) &= (byte)~m;
    }

    // Byte-granular span fill for FillTriangle's scanlines — same dither/coverage derivation as
    // double-buffered/Surface.cs's FillSpan (see there for the full comment); pointer-based here,
    // tilesPerRow = 16.
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
        ushort tile = (ushort)((ushort)(y >> 3) * 16 + firstByte);
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
        pixels = Mem.Alloc(3840 + 15);
        pixels = (byte*)(((ulong)pixels + 15) & ~15UL);
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
        if (Cgb.IsColor())
        {
            // CGB: LCD stays ON the whole time — no more per-frame Lcd.Off flash. The 3840-byte
            // page doesn't fit Cgb.CopyToVram's 2048-byte-per-transfer limit in one shot, so it goes
            // as two 1920-byte GDMA transfers across two consecutive vblanks (the first half at
            // $8000, the second at $8000+1920=$8780, the double-buffered demo's absolute-address
            // precedent). Each transfer is safe from the mode-3 VRAM lock the same way
            // double-buffered's CGB path is (HDMA writes, not CPU writes, so Mode3WriteGuard is
            // untouched). Accepted tradeoff: for the one frame between the two WaitVBlank calls, the
            // top half of the screen shows the new frame's content while the bottom half still shows
            // the previous frame's — a one-frame horizontal seam, not a full-frame flash. Pacing to
            // Ppu.WaitVBlank() (as before) still caps rotation speed to the hardware frame rate.
            Ppu.WaitVBlank();
            Cgb.CopyToVram(pixels, 0x8000, 1920);
            Ppu.WaitVBlank();
            Cgb.CopyToVram(pixels + 1920, 0x8780, 1920);
        }
        else
        {
            // DMG: no DMA to VRAM exists, so the LCD-off flash tradeoff stays, but the manual
            // per-byte loop becomes one Mem.Copy(Gb.Vram, pixels, 3840) call (the tuned block-loop
            // runtime; see MemRuntime.cs). Measured directly: 1,162,016 dots for the whole call, i.e.
            // ~16.5 DMG frames (70224 dots/frame) of blanked screen per render. Pacing to
            // Ppu.WaitVBlank() still aligns the flash to the start of a frame.
            Ppu.WaitVBlank();
            Lcd.Off();
            Mem.Copy(Gb.Vram, pixels, 3840);
            Lcd.On();
        }
    }
}
