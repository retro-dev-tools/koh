// Game-specific graphics for 2048: build the framed-block tileset and paint the board as a grid of
// 2x2 blocks. The generic parts — writing tile pixels and tile-map cells over VRAM — live in the
// Koh.GameBoy framework (TileData / Tilemap); this file is only the 2048-specific art and layout.

static class Tiles
{
    // The 12 background tiles: tile 0 is empty (lightest shade); tiles 1..11 are framed blocks whose
    // interior shade cycles so adjacent values read differently.
    internal static void GenerateTileset()
    {
        TileData.Clear(0);

        for (byte t = 1; t < 12; t++)
        {
            byte c = (byte)(1 + (t % 3)); // interior shade 1..3
            byte fillLo = ((c & 1) != 0) ? (byte)0x7E : (byte)0x00; // interior bits 1..6
            byte fillHi = ((c & 2) != 0) ? (byte)0x7E : (byte)0x00;

            for (byte row = 0; row < 8; row++)
            {
                byte low;
                byte high;
                if (row == 0 || row == 7)
                {
                    low = 0xFF; // top/bottom frame row: colour 3
                    high = 0xFF;
                }
                else
                {
                    low = (byte)(fillLo | 0x81); // interior + left/right frame pixels
                    high = (byte)(fillHi | 0x81);
                }
                TileData.SetRow(t, row, low, high);
            }
        }
    }

    // Paint the board: each cell is drawn as a 2x2 block of its value's tile, spaced into a grid.
    internal static void RenderBoard()
    {
        for (byte r = 0; r < 4; r++)
        for (byte c = 0; c < 4; c++)
        {
            byte tile = Board.Tile(r, c);
            byte baseRow = (byte)(3 + r * 3);
            byte baseCol = (byte)(3 + c * 4);
            for (byte dr = 0; dr < 2; dr++)
            for (byte dc = 0; dc < 2; dc++)
                Tilemap.SetTile((byte)(baseCol + dc), (byte)(baseRow + dr), tile);
        }
    }
}
