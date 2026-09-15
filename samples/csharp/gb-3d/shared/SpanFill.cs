static unsafe class SpanFill
{
    // Byte-granular span fill for FillTriangle's scanlines, replacing a per-pixel SetPixel loop with
    // bit-exact output. Shared by all three Surface variants (double-buffered, full-frame,
    // racing-beam) — identical dither/coverage math, differing only in each demo's tilemap row stride
    // (tilesPerRow: 12/16/8 tiles per row). Derivation:
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
    //    byte-columns are +16 apart (each Surface's SetPixel `offset`/`o` derivation follows the same
    //    `tile * 16 + (y & 7) * 2` layout).
    internal static void Fill(byte* pixels, byte y, byte xa, byte xb, byte color, byte tilesPerRow)
    {
        byte dither = color > 1 ? (byte)(0x88 >> (y & 3)) : (byte)0x00;
        byte fullBit0 = (color & 1) != 0 ? (byte)0xFF : (byte)0x00;
        byte plane0 = (byte)(fullBit0 ^ dither);
        byte bit1Dither = (color & 1) == 0 ? dither : (byte)0x00;
        byte fullBit1 = (color & 2) != 0 ? (byte)0xFF : (byte)0x00;
        byte plane1 = (byte)(fullBit1 ^ bit1Dither);

        byte firstByte = (byte)(xa >> 3);
        byte lastByte = (byte)(xb >> 3);
        ushort tile = (ushort)((ushort)(y >> 3) * tilesPerRow + firstByte);
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
}
