using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// Draws text using the font loaded by <see cref="Font.LoadDefault"/> (graphics-library design doc §3
/// "Font.cs / Text.cs") — the confirmed hole that neither existing sample could fill at all. A glyph
/// index is <c>Font.FirstTile + ((byte)ch - Font.FirstCodePoint)</c>; every <c>Draw</c> overload below
/// walks its text source left to right and writes one tile per character via <see cref="Bg.SetTile"/>/
/// <see cref="Win.SetTile"/> (both already immediate-checked — <see cref="Text"/> adds no extra gating).
///
/// <b>String overloads work because strings flow across CIL call boundaries</b> (graphics-library design
/// doc §8, resolved decision 3): <paramref name="text"/> is a pointer to a length-prefixed ROM blob, so
/// <c>.Length</c>/the indexer inside THIS method's own body are ordinary runtime memory reads off
/// whatever pointer value the caller passed — a literal (<c>Text.Draw(1, 0, "SCORE")</c>) is exactly as
/// valid a receiver as any other string value. See <c>CilStringLiteralTests</c>'s
/// <c>TextDrawShapedLibraryCall_StringParameterAcrossCallBoundary_CompilesAndRuns</c> fixture, which is
/// this exact shape. The <see cref="Draw(byte, byte, byte[], byte)"/> overload is kept anyway as the
/// guaranteed-expressible fallback the design doc calls for, and for ROM tables a game already has as
/// <c>byte[]</c> (not every caller wants to allocate a string literal).
/// </summary>
public static unsafe class Text
{
    /// <summary>Draw <paramref name="text"/> to the background layer starting at
    /// (<paramref name="col"/>, <paramref name="row"/>), one tile per character, left to right.</summary>
    public static void Draw(byte col, byte row, string text)
    {
        for (int i = 0; i < text.Length; i++)
            Bg.SetTile((byte)(col + i), row, Glyph((byte)text[i]));
    }

    /// <summary>Same as <see cref="Draw(byte, byte, string)"/> but to the window layer.</summary>
    public static void DrawToWindow(byte col, byte row, string text)
    {
        for (int i = 0; i < text.Length; i++)
            Win.SetTile((byte)(col + i), row, Glyph((byte)text[i]));
    }

    /// <summary>Explicit-count <c>byte[]</c> fallback (no string literal needed): draws
    /// <paramref name="length"/> ASCII bytes from the START of <paramref name="ascii"/> to the
    /// background layer. Takes an explicit count for the same reason <see cref="TileSet"/>'s overloads
    /// do — <c>.Length</c> on a <c>byte[]</c> PARAMETER has no runtime representation in this subset (see
    /// <c>TileSet</c>'s class remarks); only <c>string</c> carries a length prefix.</summary>
    public static void Draw(byte col, byte row, byte[] ascii, byte length)
    {
        if (length == 0)
            return;
        fixed (byte* source = &ascii[0])
        {
            for (byte i = 0; i < length; i++)
                Bg.SetTile((byte)(col + i), row, Glyph(*(source + i)));
        }
    }

    /// <summary>Decimal, left-aligned: draws exactly as many tiles as <paramref name="value"/> has
    /// digits (1-5), no padding.</summary>
    public static void DrawNumber(byte col, byte row, ushort value) =>
        WriteNumber(col, row, value, 0, toWindow: false);

    /// <summary>Decimal, right-aligned within <paramref name="width"/> tiles: space-pads on the left
    /// when <paramref name="value"/> has fewer digits than <paramref name="width"/>; a value with MORE
    /// digits than <paramref name="width"/> draws in full (no truncation), same "caller's responsibility"
    /// stance as the rest of this library.</summary>
    public static void DrawNumber(byte col, byte row, ushort value, byte width) =>
        WriteNumber(col, row, value, width, toWindow: false);

    /// <summary>Same as <see cref="DrawNumber(byte, byte, ushort)"/> but to the window layer — the
    /// <see cref="DrawToWindow"/> counterpart <see cref="DrawNumber(byte, byte, ushort)"/> was missing:
    /// a HUD showing a live number (e.g. a score) next to window text has no way to keep both on the
    /// same layer without this (design doc §5 demo item 2, "a window HUD showing SCORE 01234").</summary>
    public static void DrawNumberToWindow(byte col, byte row, ushort value) =>
        WriteNumber(col, row, value, 0, toWindow: true);

    /// <summary>Same as <see cref="DrawNumber(byte, byte, ushort, byte)"/> but to the window layer.</summary>
    public static void DrawNumberToWindow(byte col, byte row, ushort value, byte width) =>
        WriteNumber(col, row, value, width, toWindow: true);

    /// <summary>Shared digit-extraction/draw core for every <c>DrawNumber</c>/<c>DrawNumberToWindow</c>
    /// overload (<paramref name="width"/> = 0 from a 2-arg overload always yields zero padding, since a
    /// value always has at least 1 digit — so one implementation serves all four). No BCL formatting:
    /// digits come out least-significant-first via repeated <c>% 10</c> / <c>/ 10</c> (manual, matching
    /// this subset's "no BCL" arithmetic stance elsewhere, e.g. <c>TileSet</c>'s bit-plane expansion),
    /// landed in a small <c>stackalloc</c> buffer (a proven CIL-frontend shape — see
    /// <c>CilStringLiteralTests.WriteAsciiSource</c>), then drawn most-significant-first. <c>ushort</c>'s
    /// max (65535) is 5 digits, so a 5-byte buffer always suffices. <paramref name="toWindow"/> picks
    /// <see cref="Win.SetTile"/> over <see cref="Bg.SetTile"/> — both already immediate-checked.</summary>
    private static void WriteNumber(byte col, byte row, ushort value, byte width, bool toWindow)
    {
        byte* digits = stackalloc byte[5];
        byte count = 0;
        if (value == 0)
        {
            digits[0] = 0;
            count = 1;
        }
        else
        {
            ushort v = value;
            while (v > 0)
            {
                digits[count] = (byte)(v % 10);
                v /= 10;
                count++;
            }
        }

        byte c = col;
        byte pad = width > count ? (byte)(width - count) : (byte)0;
        for (byte i = 0; i < pad; i++)
        {
            SetGlyphTile(c, row, (byte)' ', toWindow);
            c++;
        }
        for (byte i = count; i > 0; i--)
        {
            SetGlyphTile(c, row, (byte)('0' + digits[i - 1]), toWindow);
            c++;
        }
    }

    private static void SetGlyphTile(byte col, byte row, byte ch, bool toWindow)
    {
        if (toWindow)
            Win.SetTile(col, row, Glyph(ch));
        else
            Bg.SetTile(col, row, Glyph(ch));
    }

    /// <summary>Maps an ASCII byte to its VRAM tile index: <see cref="Font.FirstTile"/> plus the
    /// character's offset into the 0x20-0x7F table (design doc §3: "glyph = fontBase + ((byte)ch -
    /// 0x20)").</summary>
    private static byte Glyph(byte ch) => (byte)(Font.FirstTile + (ch - Font.FirstCodePoint));
}
