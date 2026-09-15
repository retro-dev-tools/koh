namespace Koh.GameBoy.Graphics;

/// <summary>
/// Composes the CGB per-tile attribute byte written by <see cref="Bg.SetAttr"/>/<see cref="Bg.FillAttr"/>
/// and <see cref="Win.SetAttr"/> (graphics-library design doc §3 "Bg.cs / Win.cs"). Bit 7 = BG-to-OBJ
/// priority, bit 6 = vertical flip, bit 5 = horizontal flip, bits 0-2 = one of the 8 CGB background
/// palettes (bit 3, VRAM bank, is NOT exposed here — that bit selects which bank the TILE DATA for this
/// cell reads from, an orthogonal concern to attribute authoring, and every module in this library
/// sources tile data from bank 0 only).
/// </summary>
public static class TileAttr
{
    public const byte FlipX = 0x20;
    public const byte FlipY = 0x40;
    public const byte Priority = 0x80;

    /// <summary>Isolates the 3-bit CGB background palette index (0-7) from <paramref name="n"/>, so a
    /// caller can pass any byte and still compose a legal attribute: <c>TileAttr.Palette(2) | TileAttr.FlipX</c>.</summary>
    public static byte Palette(byte n) => (byte)(n & 7);
}
