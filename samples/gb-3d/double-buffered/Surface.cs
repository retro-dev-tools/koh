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

    // DMG fallback: VRAM bytes uploaded per vblank (see the cycle budget note in Present()).
    const byte PixelChunkSize = 3;

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
        for (ushort i = 0; i < 1920; i++)
            *(pixels + i) = 0;
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

    internal static void Initialize()
    {
        pixels = Mem.Alloc(1920 + 15);
        pixels = (byte*)(((ulong)pixels + 15) & ~15UL);
        Lcd.Off();
        // Both pixel pages start as garbage (the DMG boot ROM leaves its logo tiles inside page 0's
        // range): clear them, plus the shared blank border tile at $8FF0.
        for (ushort i = 0; i < 1920; i++)
        {
            *(Gb.Vram + i) = 0;
            *(Gb.Vram + 0x1000 + i) = 0;
        }
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
            // DMG fallback: no DMA to VRAM exists, so keep the timing-safe vblank-chunked CPU
            // upload. Each compiled `*(Gb.Vram + ...) = *(pixels + ...)` iteration costs ~820 dots
            // (measured against this codegen) — more than mode 0's worst-case 204-dot window, so
            // per-hblank bursts can't work; only vblank's ~4104 usable dots (10 lines minus
            // WaitVBlank()'s edge-detection line) fit a chunk, and 3 bytes (2 x 820 dots between
            // first and last write) leaves wide margin where a chunk of 5 produced sporadic real
            // mode-3 writes. Slow — a page takes 1920/3 = 640 frames (~11 s) — but tear-free and
            // zero mode-3 writes; accepted for the monochrome fallback. The LCDC.4 flip still
            // applies here and removes the old 120-cell tilemap rewrite (60 more vblanks) per flip.
            ushort baseOffset = page == 0 ? (ushort)0x0000 : (ushort)0x1000;
            ushort i = 0;
            while (i < 1920)
            {
                Ppu.WaitVBlank();
                for (byte k = 0; k < PixelChunkSize && i < 1920; k++)
                {
                    *(Gb.Vram + baseOffset + i) = *(pixels + i);
                    i++;
                }
            }
            // The final chunk leaves us ~2500 dots into its vblank (3 writes x ~820 dots), still
            // well inside the 4560-dot window: flip immediately, no extra frame.
            Lcd.SelectTileData(page == 0);
        }
        page ^= 1;
    }
}
