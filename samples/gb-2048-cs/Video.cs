// A tiny Game Boy drawing library: generate the tileset, paint the board into the background tile
// map, and synchronise with vertical blank. It reads the board through Board's typed API and writes
// the video regions through the Gb.* surface, so the game's main loop stays free of both.

static unsafe class Video
{
    // Build 12 background tiles at the tile-data region: tile 0 is empty (lightest shade); tiles 1..11
    // are framed blocks whose interior shade cycles so adjacent values read differently. Each tile is
    // 8 rows x 2 bit-planes = 16 bytes.
    internal static void GenerateTiles()
    {
        byte* vram = Gb.Vram;

        for (byte i = 0; i < 16; i++)
            *(vram + i) = 0; // tile 0: all colour 0

        for (byte t = 1; t < 12; t++)
        {
            byte c = (byte)(1 + (t % 3)); // interior shade 1..3
            byte fillLo = ((c & 1) != 0) ? (byte)0x7E : (byte)0x00; // interior bits 1..6
            byte fillHi = ((c & 2) != 0) ? (byte)0x7E : (byte)0x00;

            byte* p = vram + t * 16;
            for (byte row = 0; row < 8; row++)
            {
                byte lo;
                byte hi;
                if (row == 0 || row == 7)
                {
                    lo = 0xFF; // top/bottom frame row: colour 3
                    hi = 0xFF;
                }
                else
                {
                    lo = (byte)(fillLo | 0x81); // interior + left/right frame pixels
                    hi = (byte)(fillHi | 0x81);
                }
                *(p + row * 2) = lo;
                *(p + row * 2 + 1) = hi;
            }
        }
    }

    // Write one tile index into the background tile map at (col, row). A map row is 32 tiles, so the
    // offset needs 16-bit math: widen row *before* the multiply, or row*32 overflows a byte for the
    // lower rows (e.g. row 12 -> 384 would truncate to 128 in 8-bit) and scribbles the wrong cell.
    static void PutTile(byte col, byte row, byte tile)
    {
        ushort off = (ushort)((ushort)row * 32 + col);
        *(Gb.TileMap + off) = tile;
    }

    // Paint the whole board: each cell is a 2x2 block of its value's tile, spaced into a grid.
    internal static void Render()
    {
        for (byte r = 0; r < 4; r++)
        {
            for (byte c = 0; c < 4; c++)
            {
                byte tile = Board.Tile(r, c);
                byte baseRow = (byte)(3 + r * 3);
                byte baseCol = (byte)(3 + c * 4);
                for (byte dr = 0; dr < 2; dr++)
                for (byte dc = 0; dc < 2; dc++)
                    PutTile((byte)(baseCol + dc), (byte)(baseRow + dr), tile);
            }
        }
    }

    // Spin until the LCD enters vertical blank, so tile-map writes don't tear.
    internal static void WaitVBlank()
    {
        while (Hardware.LY == 144) { } // leave the current vblank, if in one
        while (Hardware.LY != 144) { } // wait for the next one
    }
}
