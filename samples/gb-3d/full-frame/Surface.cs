static unsafe class Surface
{
    // 16 cols x 15 rows of content tiles (0..239) instead of a full 16x16 (0..255): giving up one tile
    // row of drawing area buys 16 spare tile-data slots (240..255) that are never part of the rotating
    // content, so they can hold a genuinely static blank tile instead of a byte that's overwritten (and
    // would otherwise flash live cube pixels) every frame. The DMG boot hand-off leaves the cartridge
    // logo in tiles 1-24 — inside the content range, mapped into the visible grid right away by
    // Initialize — so Initialize also zeroes 0..239, not just the spare slots.
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

    // Byte-granular span fill for FillTriangle's scanlines; the dither/coverage derivation and the
    // full body live once in shared/SpanFill.cs (see there), since it's identical across all three
    // Surface variants except for the tilemap row stride. tilesPerRow = 16 (16x15 content grid).
    internal static void FillSpan(byte y, byte xa, byte xb, byte color)
    {
        SpanFill.Fill(pixels, y, xa, xb, color, 16);
    }

    internal static void Initialize()
    {
        pixels = Mem.Alloc(3840 + 15);
        pixels = (byte*)(((ulong)pixels + 15) & ~15UL);
        Lcd.Off();
        // Zero the content tile range too (not just the spare slots below): the boot hand-off leaves
        // the cartridge logo in tiles 1-24, which sit inside 0..239 and get mapped into the visible
        // grid below, so without this the logo would flash for the frames before the first Present()
        // overwrites it. LCD is off here, so the bulk VRAM write is safe regardless of PPU mode.
        Mem.Fill(Gb.Vram, 0, 3840);
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
