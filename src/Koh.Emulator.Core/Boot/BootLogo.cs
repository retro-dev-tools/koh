namespace Koh.Emulator.Core.Boot;

/// <summary>
/// Decompresses the 48-byte Nintendo logo bitmap every cartridge header
/// embeds at $0104-$0133 into Game Boy tile data. This is NOT boot ROM code
/// and contains none of Nintendo's copyrighted boot ROM bytes: it is a
/// from-scratch implementation of the publicly documented bitmap format
/// (Pan Docs, "The Cartridge Header" — Nintendo logo field) applied to data
/// the cartridge itself already legally contains. Used to model what the
/// real (skipped) boot ROM leaves in VRAM at hand-off, and to draw the
/// visible HLE boot animation.
/// </summary>
public static class BootLogo
{
    public const int TileColumns = 12;
    public const int TileRows = 2;
    public const int TileCount = TileColumns * TileRows; // 24
    public const int TileDataBytes = TileCount * 16;

    /// <summary>
    /// Unpacks a 48-byte header logo field into 24 8x8 2bpp tiles (16 bytes
    /// each; both bitplanes are written identically, so every pixel is flat
    /// color id 0 or 3 — matching the real logo before the boot ROM's
    /// palette-fade animation introduces grays). Tiles are emitted row-major:
    /// index 0 = top-left tile, 11 = top-right, 12 = bottom-left, 23 =
    /// bottom-right.
    /// </summary>
    /// <remarks>
    /// Per Pan Docs: bytes 0-23 encode the top half of the logo, bytes 24-47
    /// the bottom half. Within each half, each nibble encodes a horizontal
    /// run of 4 pixels (MSB = leftmost), and the 4-pixel groups are laid out
    /// top-to-bottom within a column before moving to the next column to the
    /// right — i.e. 12 columns x 4 nibbles(rows) per half. The whole raw
    /// 48x8 bitmap (12 columns x 4px wide, 4px x 2 halves tall) is then
    /// upscaled by a uniform factor of 2 to 96x16 — exactly 12x2 tiles.
    /// </remarks>
    public static byte[] Decompress(ReadOnlySpan<byte> headerLogo)
    {
        if (headerLogo.Length < 48)
            throw new ArgumentException("logo field must be 48 bytes", nameof(headerLogo));

        // Raw (pre-2x) bitmap: 8 rows (4 per half) x 48 columns (12 groups x 4px).
        Span<bool> raw = stackalloc bool[8 * 48];
        for (int half = 0; half < 2; half++)
        {
            for (int n = 0; n < 48; n++)
            {
                byte srcByte = headerLogo[half * 24 + n / 2];
                int nibble = (n % 2 == 0) ? (srcByte >> 4) & 0xF : srcByte & 0xF;
                int col = n / 4;
                int rowInHalf = n % 4;
                int row = half * 4 + rowInHalf;
                for (int k = 0; k < 4; k++)
                    raw[row * 48 + col * 4 + k] = ((nibble >> (3 - k)) & 1) != 0;
            }
        }

        var tiles = new byte[TileDataBytes];
        for (int trow = 0; trow < TileRows; trow++)
        for (int tcol = 0; tcol < TileColumns; tcol++)
        {
            int tileIdx = trow * TileColumns + tcol;
            for (int py = 0; py < 8; py++)
            {
                int rawRow = trow * 4 + py / 2; // uniform 2x vertical upscale
                byte rowByte = 0;
                for (int px = 0; px < 8; px++)
                {
                    int rawCol = tcol * 4 + px / 2; // uniform 2x horizontal upscale
                    if (raw[rawRow * 48 + rawCol])
                        rowByte |= (byte)(0x80 >> px);
                }
                int rowOffset = tileIdx * 16 + py * 2;
                tiles[rowOffset] = rowByte; // bitplane 0
                tiles[rowOffset + 1] = rowByte; // bitplane 1 (identical -> flat pixel)
            }
        }
        return tiles;
    }
}
