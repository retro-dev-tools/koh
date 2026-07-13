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
    const byte PixelChunkSize = 4;

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

    // Byte-granular span fill for FillTriangle's scanlines, replacing a per-pixel SetPixel loop with
    // bit-exact output. Derivation:
    //  - Within an 8-pixel byte, pixel p (bit 7-p) satisfies x&3 == p&3 (byte columns are 8-pixel
    //    aligned, so p == x&7), so the ordered-dither positions for a given row y — SetPixel's
    //    `((x^y)&3)==0` rule — form exactly the byte mask `dither = 0x88 >> (y & 3)` (2 bits set),
    //    applied only when color > 1 (color 1 never dims to color 0 under this rule).
    //  - `color - 1` always flips bit 0 of the color and flips bit 1 iff (color & 1) == 0 (color is
    //    even). So for a byte-column fully covered by the span, the two plane bytes are:
    //      plane0 = (color bit0 ? 0xFF : 0x00) XOR dither
    //      plane1 = (color bit1 ? 0xFF : 0x00) XOR (color even ? dither : 0x00)
    //  - A byte-column only partially covered (the span's first/last, or both when the whole span sits
    //    inside one byte-column) is written read-modify-write against a coverage mask built from the
    //    same two shifts: `cover = (0xFF >> (xa & 7)) & (0xFF << (7 - (xb & 7)))`, computed ONCE per
    //    span (not per pixel/byte) since a variable-count shift lowers to a loop on SM83.
    //  - Interior byte-columns are fully covered by construction and get direct plane stores; adjacent
    //    byte-columns are +16 apart (see SetPixel's `offset` derivation just above).
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
        ushort tile = (ushort)((ushort)(y >> 3) * 12 + firstByte);
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
            // upload. Each compiled `*(Gb.Vram + ...) = *(pixels + ...)` iteration cost ~820 dots
            // against the original per-instruction codegen; the SM83 backend now fuses a single-use
            // `gep` directly into the load/store it feeds instead of round-tripping it through a WRAM
            // slot (Sm83Backend.EmitContext.FusedGep), which measures ~645 dots/iteration for this
            // exact loop shape (baseOffset + i, dmg, single-bank) — about 11% less. Still more than
            // mode 0's worst-case 204-dot window, so per-hblank bursts can't work; only vblank's
            // ~4104 usable dots (10 lines minus WaitVBlank()'s edge-detection line) fit a chunk. 4
            // bytes (3 x ~645 dots = ~1935 between first and last write) still leaves comfortable
            // margin (~2169 dots) — re-verified against Cube3dVerify's Mode3WriteGuard, which fails on
            // any real VRAM write during mode 3 — where a chunk of 5 produced sporadic real mode-3
            // writes under the older, slower codegen (a chunk of 5 now costs 4 x ~645 = ~2580, likely
            // fine too, but not re-validated here — leave at 4 unless a future measurement re-checks
            // it). Slow — a page takes 1920/4 = 480 frames (~8.5 s) — but tear-free and zero mode-3
            // writes; accepted for the monochrome fallback. The LCDC.4 flip still applies here and
            // removes the old 120-cell tilemap rewrite (60 more vblanks) per flip.
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
