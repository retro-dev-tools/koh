namespace Koh.GameBoy;

/// <summary>Background tile pixel data ($8000): each tile is 8 rows of two bit-planes (16 bytes). A
/// typed view over that VRAM region so callers build tiles a row at a time instead of poking bytes.</summary>
public static unsafe class TileData
{
    /// <summary>Zero every pixel of a tile (all colour 0). The offset is widened to 16-bit before the
    /// multiply (tile 16 -> 256 would overflow a byte and clear the wrong tile's data).</summary>
    public static void Clear(byte tile)
    {
        byte* p = Gb.Vram + (ushort)((ushort)tile * 16);
        for (byte i = 0; i < 16; i++)
            *(p + i) = 0;
    }

    /// <summary>Write one 8-pixel row of a tile as its two bit-planes (low, high).</summary>
    public static void SetRow(byte tile, byte row, byte low, byte high)
    {
        byte* p = Gb.Vram + (ushort)((ushort)tile * 16 + row * 2);
        *p = low;
        *(p + 1) = high;
    }
}
