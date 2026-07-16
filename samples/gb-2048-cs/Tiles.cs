// Game-specific graphics for 2048: the framed-block board tileset, a small cursor sprite shape, and
// painting the board as a grid of 2x2 blocks. The generic parts — bulk tile loading and tilemap
// rect-fills — live in the Koh.GameBoy.Graphics library (TileSet / Bg); this file is only the
// 2048-specific art and layout.
using Koh.GameBoy.Graphics;

namespace Koh.Samples.Gb2048CSharp;

static class Tiles
{
    // The 12 background tiles: tile 0 is empty (lightest shade); tiles 1..11 are framed blocks whose
    // interior shade cycles so adjacent values read differently. Authored as literal ROM data — the
    // exact pixels the old GenerateTileset() built at runtime via per-row TileData.SetRow pokes — so
    // TileSet.Load can copy them into VRAM in one call instead.
    internal const byte BoardTileCount = 12;

    private static readonly byte[] BoardTiles =
    {
        // tile 0 (empty)
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        0x00,
        // tile 1
        0xFF,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0xFF,
        0xFF,
        // tile 2
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        // tile 3
        0xFF,
        0xFF,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0xFF,
        // tile 4
        0xFF,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0xFF,
        0xFF,
        // tile 5
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        // tile 6
        0xFF,
        0xFF,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0xFF,
        // tile 7
        0xFF,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0xFF,
        0xFF,
        // tile 8
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        // tile 9
        0xFF,
        0xFF,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0xFF,
        // tile 10
        0xFF,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0x81,
        0xFF,
        0xFF,
        0xFF,
        // tile 11
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
    };

    // A small filled-diamond sprite tile — the cursor Game.cs positions over the most recently spawned
    // cell (Board.CursorRow/CursorCol), previously impossible: no C# code touched Gb.Oam at all.
    internal const byte CursorSpriteTile = BoardTileCount; // tile 12

    private static readonly byte[] CursorTile =
    {
        0x00,
        0x00,
        0x18,
        0x18,
        0x3C,
        0x3C,
        0x7E,
        0x7E,
        0x7E,
        0x7E,
        0x3C,
        0x3C,
        0x18,
        0x18,
        0x00,
        0x00,
    };

    // The 96-glyph ASCII font follows the board art + cursor tile, well clear of both.
    internal const byte FontFirstTile = CursorSpriteTile + 1; // tile 13..108

    // Load every 2048-specific tile into VRAM: board art at tile 0, the cursor sprite right after it.
    internal static void Load()
    {
        TileSet.Load(0, BoardTiles, BoardTileCount);
        TileSet.Load(CursorSpriteTile, CursorTile, 1);
    }

    // Paint the board: each cell is drawn as a 2x2 block of its value's tile, spaced into a grid.
    internal static void RenderBoard()
    {
        for (byte r = 0; r < 4; r++)
        for (byte c = 0; c < 4; c++)
            Bg.Fill(ColOf(c), RowOf(r), 2, 2, Board.Tile(r, c));
    }

    // Screen-pixel top-left of board cell (row, col)'s 2x2 tile block — what Game.cs positions the
    // sprite cursor at over Board.CursorRow/CursorCol. Kept alongside RenderBoard so the two can never
    // drift apart: both derive from the same ColOf/RowOf tile-grid layout.
    internal static int CellPixelX(byte col) => ColOf(col) * 8;

    internal static int CellPixelY(byte row) => RowOf(row) * 8;

    private static byte ColOf(byte c) => (byte)(3 + c * 4);

    private static byte RowOf(byte r) => (byte)(3 + r * 3);
}
