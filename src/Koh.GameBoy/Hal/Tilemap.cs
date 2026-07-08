namespace Koh.GameBoy;

/// <summary>The background tile map (32x32 tile indices at $9800): a typed view over that VRAM region
/// so callers write <c>Tilemap.Set(col, row, tile)</c> instead of computing raw addresses.</summary>
public static unsafe class Tilemap
{
    /// <summary>Set the tile index at (col, row). A map row is 32 tiles, so the offset is widened to
    /// 16-bit before the multiply (row 12 -> 384 would overflow a byte and scribble the wrong cell).</summary>
    public static void Set(byte col, byte row, byte tile)
    {
        ushort offset = (ushort)((ushort)row * 32 + col);
        *(Gb.TileMap + offset) = tile;
    }
}
