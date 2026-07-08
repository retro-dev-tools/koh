// Lays out several 160x144 GB framebuffers into a single PNG annotated with
// per-frame labels and an optional border. Use this from a verify script to
// produce one composite "all states at a glance" image instead of N PNGs.
using System.IO;

namespace Koh.Verify;

public static class ContactSheet
{
    public sealed record Tile(string Label, byte[] Rgb);

    /// <summary>Lay out <paramref name="tiles"/> in a grid and write a PNG.</summary>
    /// <summary>Horizontal strip — each frame in a single row, labelled.</summary>
    public static void WriteStrip(
        string path,
        IList<Tile> tiles,
        int scale = 2,
        int padding = 6,
        int labelHeight = 14,
        byte bgR = 24,
        byte bgG = 24,
        byte bgB = 28,
        byte labelR = 240,
        byte labelG = 240,
        byte labelB = 240
    ) =>
        Write(
            path,
            tiles,
            columns: tiles.Count,
            scale: scale,
            padding: padding,
            labelHeight: labelHeight,
            bgR: bgR,
            bgG: bgG,
            bgB: bgB,
            labelR: labelR,
            labelG: labelG,
            labelB: labelB
        );

    public static void Write(
        string path,
        IList<Tile> tiles,
        int columns,
        int scale = 2,
        int padding = 12,
        int labelHeight = 18,
        byte bgR = 24,
        byte bgG = 24,
        byte bgB = 28,
        byte labelR = 240,
        byte labelG = 240,
        byte labelB = 240
    )
    {
        const int W = 160,
            H = 144;
        int sW = W * scale,
            sH = H * scale;
        int rows = (tiles.Count + columns - 1) / columns;
        int cellW = sW + padding * 2;
        int cellH = sH + padding * 2 + labelHeight;
        int totalW = cellW * columns;
        int totalH = cellH * rows;

        var canvas = new byte[totalW * totalH * 4];
        // Fill background.
        for (int p = 0; p < totalW * totalH; p++)
        {
            canvas[p * 4 + 0] = bgR;
            canvas[p * 4 + 1] = bgG;
            canvas[p * 4 + 2] = bgB;
            canvas[p * 4 + 3] = 0xFF;
        }

        // Paint each tile.
        for (int idx = 0; idx < tiles.Count; idx++)
        {
            int row = idx / columns;
            int col = idx % columns;
            int x0 = col * cellW + padding;
            int y0 = row * cellH + padding + labelHeight;
            BlitScaledRgb(canvas, totalW, x0, y0, tiles[idx].Rgb, W, H, scale);
            DrawLabel(
                canvas,
                totalW,
                totalH,
                col * cellW + padding,
                row * cellH + 4,
                cellW - padding * 2,
                labelHeight,
                tiles[idx].Label,
                labelR,
                labelG,
                labelB
            );
        }
        PngEncoder.WriteFromRgba(path, canvas, totalW, totalH, scale: 1);
    }

    private static void BlitScaledRgb(
        byte[] dst,
        int dstStride,
        int x0,
        int y0,
        byte[] src,
        int srcW,
        int srcH,
        int scale
    )
    {
        for (int sy = 0; sy < srcH; sy++)
        for (int sx = 0; sx < srcW; sx++)
        {
            int i = (sy * srcW + sx) * 3;
            byte r = src[i],
                g = src[i + 1],
                b = src[i + 2];
            for (int dy = 0; dy < scale; dy++)
            for (int dx = 0; dx < scale; dx++)
            {
                int dpos = ((y0 + sy * scale + dy) * dstStride + x0 + sx * scale + dx) * 4;
                dst[dpos + 0] = r;
                dst[dpos + 1] = g;
                dst[dpos + 2] = b;
                dst[dpos + 3] = 0xFF;
            }
        }
    }

    // Compact 5x7 ASCII font for labels (uppercase + digits + a few symbols).
    // 1-bit per pixel, packed as one byte per row.
    private static readonly Dictionary<char, byte[]> Glyphs = BuildGlyphs();

    private static Dictionary<char, byte[]> BuildGlyphs()
    {
        // Each glyph: 7 rows of a byte where bit 4 (mask 0x10) is leftmost pixel.
        // Defining only the characters we need.
        var g = new Dictionary<char, byte[]>();
        void Add(char c, params byte[] rows) => g[c] = rows;
        Add('A', 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001);
        Add('B', 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110);
        Add('C', 0b01111, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b01111);
        Add('D', 0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110);
        Add('E', 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111);
        Add('F', 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000);
        Add('G', 0b01111, 0b10000, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111);
        Add('H', 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001);
        Add('I', 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110);
        Add('K', 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001);
        Add('L', 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111);
        Add('M', 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001);
        Add('N', 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001);
        Add('O', 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110);
        Add('P', 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000);
        Add('R', 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001);
        Add('S', 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110);
        Add('T', 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100);
        Add('U', 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110);
        Add('V', 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b01010, 0b00100);
        Add('W', 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001);
        Add('Y', 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100);
        Add('-', 0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000);
        Add('_', 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b11111);
        Add(' ', 0, 0, 0, 0, 0, 0, 0);
        Add('0', 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110);
        Add('1', 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110);
        Add('2', 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111);
        Add('3', 0b01110, 0b10001, 0b00001, 0b00110, 0b00001, 0b10001, 0b01110);
        Add('4', 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010);
        Add('5', 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110);
        Add('6', 0b00110, 0b01000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110);
        Add('7', 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000);
        Add('8', 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110);
        Add('9', 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00010, 0b01100);
        Add(':', 0, 0b00100, 0b00100, 0, 0b00100, 0b00100, 0);
        Add('.', 0, 0, 0, 0, 0, 0b00100, 0b00100);
        Add(',', 0, 0, 0, 0, 0, 0b00100, 0b01000);
        Add('(', 0b00010, 0b00100, 0b01000, 0b01000, 0b01000, 0b00100, 0b00010);
        Add(')', 0b01000, 0b00100, 0b00010, 0b00010, 0b00010, 0b00100, 0b01000);
        Add('=', 0b00000, 0b00000, 0b11111, 0b00000, 0b11111, 0b00000, 0b00000);
        return g;
    }

    private static void DrawLabel(
        byte[] canvas,
        int w,
        int h,
        int x,
        int y,
        int areaW,
        int areaH,
        string text,
        byte r,
        byte g,
        byte b
    )
    {
        string up = text.ToUpperInvariant();
        const int charW = 6,
            charH = 8,
            glyphScale = 2;
        int textW = up.Length * charW * glyphScale;
        int sx = x + Math.Max(0, (areaW - textW) / 2);
        int sy = y + (areaH - charH * glyphScale) / 2;
        foreach (char c in up)
        {
            if (!Glyphs.TryGetValue(c, out var rows))
                rows = Glyphs[' '];
            for (int ry = 0; ry < 7; ry++)
            {
                byte row = rows[ry];
                for (int rx = 0; rx < 5; rx++)
                {
                    bool on = (row & (1 << (4 - rx))) != 0;
                    if (!on)
                        continue;
                    int px = sx + rx * glyphScale;
                    int py = sy + ry * glyphScale;
                    for (int dy = 0; dy < glyphScale; dy++)
                    for (int dx = 0; dx < glyphScale; dx++)
                    {
                        int cx = px + dx,
                            cy = py + dy;
                        if ((uint)cx >= w || (uint)cy >= h)
                            continue;
                        int p = (cy * w + cx) * 4;
                        canvas[p + 0] = r;
                        canvas[p + 1] = g;
                        canvas[p + 2] = b;
                        canvas[p + 3] = 0xFF;
                    }
                }
            }
            sx += charW * glyphScale;
        }
    }
}
