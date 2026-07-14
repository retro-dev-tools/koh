static unsafe class Surface
{
    // One STATIC 12x10 tilemap window (indexes 0..119) drives both pixel pages — the classic
    // LCDC.4 double-buffer trick: page 0's pixel data lives at $8000-$877F (reached while LCDC
    // bit 4 = 1, "$8000 addressing"), page 1's at $9000-$977F (the same indexes 0..119 while
    // bit 4 = 0, "$8800 addressing"; indexes 0..127 there read $9000-$97FF, NOT a signed offset).
    // Flipping pages is therefore one LCDC register write during vblank instead of a 120-cell
    // tilemap rewrite. Index 255 resolves to $8FF0 under BOTH addressing modes (indexes 128..255
    // share $8800-$8FFF), so a single blank tile pads the border cells whichever page is showing.
    const byte BlankTile = 255;

    // DMG fallback: VRAM bytes uploaded per vblank via one Mem.Copy call (see the cycle budget note
    // in Present()). Sized by measuring whole Mem.Copy(dst, src, n) calls directly (two calls minus
    // one, to strip shared setup) rather than extrapolating a per-byte rate: cost(n) = 1528 + 300n
    // dots (measured for n = 4..24; matches the tuned block-loop runtime's ~300 dots/byte marginal
    // rate plus a ~1528-dot fixed call overhead). n=7 costs 3628 dots, leaving 476 of the ~4104-dot
    // usable vblank — over the 300-dot margin; n=8 costs 3928, leaving only 176 (under margin). Up
    // from the old 4-bytes/vblank manual-loop drip (1920/4 = 480 frames/page); 1920/7 ~= 275
    // frames/page now.
    const byte PixelChunkSize = 7;

    // The 1920-byte render target. Allocated from the WRAM arena rather than declared as a static
    // array so its address can be handed to the CGB GDMA source registers (Cgb.CopyToVram casts it
    // to ushort; legal C# cannot take a managed array's address, and the same source must also
    // build as the desktop reference binary). Aligned up to 16 bytes because HDMA1/2 ignore the
    // low 4 source bits — the ulong math is exact on the ROM (the GB address zero-extends) and
    // stays a valid in-allocation pointer on the 64-bit desktop, where the CGB path never runs.
    static byte* pixels;
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
        Mem.Fill(pixels, 0, 1920);
    }

    internal static void SetPixel(byte x, byte y, byte color)
    {
        if (x >= 96 || y >= 80)
            return;
        ushort tile = (ushort)((ushort)(y >> 3) * 12 + (x >> 3));
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

    // Byte-granular span fill for FillTriangle's scanlines; the dither/coverage derivation and the
    // full body live once in shared/SpanFill.cs (compiled into all three demos — see the <Compile
    // Include> in each .csproj), since it's identical across all three Surface variants except for
    // the tilemap row stride. tilesPerRow = 12 (12x10 content grid — see SetPixel's `offset` above).
    internal static void FillSpan(byte y, byte xa, byte xb, byte color)
    {
        SpanFill.Fill(pixels, y, xa, xb, color, 12);
    }

    internal static void Initialize()
    {
        pixels = Mem.Alloc(1920 + 15);
        pixels = (byte*)(((ulong)pixels + 15) & ~15UL);
        Lcd.Off();
        // Both pixel pages start as garbage (the DMG boot ROM leaves its logo tiles inside page 0's
        // range): clear them, plus the shared blank border tile at $8FF0. LCD is off here, so a bulk
        // VRAM write is safe regardless of PPU mode.
        Mem.Fill(Gb.Vram, 0, 1920);
        Mem.Fill(Gb.Vram + 0x1000, 0, 1920);
        TileData.Clear(BlankTile);
        Tilemap.Clear(BlankTile);
        for (byte r = 0; r < 10; r++)
        for (byte c = 0; c < 12; c++)
            Tilemap.SetTile((byte)(4 + c), (byte)(4 + r), (byte)(r * 12 + c));
        // Lcd.On() selects $8000 addressing (page 0 visible), so the first frame renders into the
        // hidden page 1.
        page = 1;
    }

    internal static void Present()
    {
        if (Cgb.IsColor())
        {
            // CGB: one general-purpose DMA moves the whole hidden page inside a single vblank. The
            // engine copies a 16-byte block per 8 M-cycles with the CPU halted: 120 blocks x 32
            // dots = 3840 dots, inside the 4560-dot vblank (10 lines x 456) even after
            // WaitVBlank()'s edge detection eats most of the first line — a slight overrun would
            // still land in line 0's 80-dot OAM scan before mode 3 locks VRAM. The bytes go
            // through the HDMA engine, not CPU writes, so the zero-mode-3-CPU-writes guarantee
            // (Cube3dVerify's Mode3WriteGuard) is untouched; the HDMA1-5 register writes
            // themselves are I/O, safe anytime. This is what restores a per-frame page flip:
            // ~480 M-cycles per upload instead of ~640 frames of chunked CPU copying.
            ushort vramBase = page == 0 ? (ushort)0x8000 : (ushort)0x9000;
            Ppu.WaitVBlank();
            Cgb.CopyToVram(pixels, vramBase, 1920);
            Lcd.SelectTileData(page == 0); // still inside the same vblank: tear-free flip
        }
        else
        {
            // DMG fallback: no DMA to VRAM exists, so keep the timing-safe vblank-chunked upload, but
            // each chunk is now one Mem.Copy call instead of a manual per-byte loop (Mem.Copy is the
            // tuned block-loop runtime — see MemRuntime.cs — measured at ~300 dots/byte marginal, plus
            // a ~1528-dot fixed per-call overhead: cost(n) = 1528 + 300n dots, measured directly per
            // chunk size, not extrapolated from the marginal rate alone). Only vblank's ~4104 usable
            // dots (10 lines minus WaitVBlank()'s edge-detection line) fit a chunk; a hblank burst can't
            // (mode 0's worst case is 204 dots). PixelChunkSize=7 costs 3628 dots, leaving a 476-dot
            // margin (over the required 300) — re-verified against Cube3dVerify's Mode3WriteGuard, which
            // fails on any real VRAM write during mode 3. Slow — a page takes ceil(1920/7) = 275 frames
            // (~4.9 s) — but tear-free and zero mode-3 writes; accepted for the monochrome fallback (down
            // from 480 frames/page at the old 4-bytes/vblank rate). The LCDC.4 flip still applies here
            // and removes the old 120-cell tilemap rewrite (60 more vblanks) per flip.
            ushort baseOffset = page == 0 ? (ushort)0x0000 : (ushort)0x1000;
            ushort i = 0;
            while (i < 1920)
            {
                Ppu.WaitVBlank();
                ushort remaining = (ushort)(1920 - i);
                byte chunk = remaining < PixelChunkSize ? (byte)remaining : PixelChunkSize;
                Mem.Copy(Gb.Vram + baseOffset + i, pixels + i, chunk);
                i += chunk;
            }
            // The final chunk leaves comfortable margin inside its vblank (see PixelChunkSize's
            // derivation): flip immediately, no extra frame.
            Lcd.SelectTileData(page == 0);
        }
        page ^= 1;
    }
}
