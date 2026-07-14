namespace Koh.GameBoy;

/// <summary>The background tile map (32x32 tile indices at $9800): a typed view over that VRAM region
/// so callers write <c>Tilemap.SetTile(col, row, tile)</c> instead of computing raw addresses.</summary>
public static unsafe class Tilemap
{
    /// <summary>Set the tile index at (col, row). A map row is 32 tiles, so the offset is widened to
    /// 16-bit before the multiply (row 12 -> 384 would overflow a byte and scribble the wrong cell).</summary>
    public static void SetTile(byte col, byte row, byte tile)
    {
        ushort offset = (ushort)((ushort)row * 32 + col);
        *(Gb.TileMap + offset) = tile;
    }

    /// <summary>Set every cell of the full 32x32 background map to one tile. Callers use this to blank
    /// the whole map to a known tile before drawing their own (smaller) visible window over it, so cells
    /// outside that window — including ones a horizontal scroll can wrap into view — show the blank tile
    /// instead of whatever the map held before init (boot-logo remnants on real hardware).</summary>
    public static void Clear(byte tile)
    {
        for (byte row = 0; row < 32; row++)
        for (byte col = 0; col < 32; col++)
            SetTile(col, row, tile);
    }
}
