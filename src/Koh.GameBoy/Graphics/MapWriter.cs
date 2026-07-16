using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// The one internal helper <see cref="Bg"/> and <see cref="Win"/> both delegate to (graphics-library
/// design doc §3 "Bg.cs / Win.cs": "Both delegate to ONE internal MapWriter(byte* mapBase, ...)"), so the
/// rect-fill/blit/attribute logic exists exactly once regardless of which of the two 32x32 tile maps
/// (<see cref="Gb.TileMap"/> at $9800 or <see cref="Gb.TileMap1"/> at $9C00) it targets. Not public — the
/// map pointer is an implementation seam, not API surface; games call <see cref="Bg"/>/<see cref="Win"/>.
///
/// Every write here is immediate-checked (graphics-library design doc §2 "Write-safety stance"): a plain
/// tile-index write is a single byte, gated on <see cref="Ppu.WaitForVramAccess"/> right before it, same
/// as <see cref="TileData"/> row pokes and <see cref="Palettes"/> writes — never deferred,
/// never chunked. Attribute writes (VRAM bank 1) are CGB-only: <see cref="Video.IsCgb"/> is checked once
/// per call (not <see cref="Cgb.IsColor"/> again — Graphics never re-derives the KEY1 check) and the whole
/// call is a true no-op on DMG, because DMG has no bank 1 to switch into — writing through
/// <paramref name="mapBase"/> with the bank still at 0 would silently clobber the TILE-INDEX map instead
/// (same physical $9800/$9C00 address), which is exactly the corruption this early-out prevents.
/// </summary>
internal static unsafe class MapWriter
{
    /// <summary>Set one cell's tile index. The row/col offset is widened to 16-bit before the multiply
    /// (row 12 -&gt; 384 would overflow a byte), matching <see cref="Tilemap.SetTile"/>'s own arithmetic —
    /// this is the $9C00 (Win) counterpart of that Hal helper, plus the internal caller bulk operations
    /// (<see cref="Fill"/>/<see cref="DrawMap"/>) route through.</summary>
    internal static void SetTile(byte* mapBase, byte col, byte row, byte tile)
    {
        Ppu.WaitForVramAccess();
        *(mapBase + Index(col, row)) = tile;
    }

    /// <summary>Rect of one tile index, <paramref name="w"/> x <paramref name="h"/> cells starting at
    /// (<paramref name="col"/>, <paramref name="row"/>).</summary>
    internal static void Fill(byte* mapBase, byte col, byte row, byte w, byte h, byte tile)
    {
        for (byte r = 0; r < h; r++)
        for (byte c = 0; c < w; c++)
            SetTile(mapBase, (byte)(col + c), (byte)(row + r), tile);
    }

    /// <summary>Blit a row-major <paramref name="w"/> x <paramref name="h"/> rect of ROM tile indices
    /// from <paramref name="tiles"/> starting at (<paramref name="col"/>, <paramref name="row"/>).</summary>
    internal static void DrawMap(byte* mapBase, byte col, byte row, byte w, byte h, byte* tiles)
    {
        for (byte r = 0; r < h; r++)
        for (byte c = 0; c < w; c++)
            SetTile(
                mapBase,
                (byte)(col + c),
                (byte)(row + r),
                *(tiles + (ushort)((ushort)r * w + c))
            );
    }

    /// <summary>Set one cell's CGB attribute byte (VRAM bank 1). Silent no-op on DMG.</summary>
    internal static void SetAttr(byte* mapBase, byte col, byte row, byte attr)
    {
        if (!Video.IsCgb)
            return;
        Ppu.WaitForVramAccess();
        Cgb.SelectVramBank(1);
        *(mapBase + Index(col, row)) = attr;
        Cgb.SelectVramBank(0);
    }

    /// <summary>Rect of one CGB attribute byte (VRAM bank 1). Silent no-op on DMG. Selects bank 1 once
    /// for the whole rect (not per-cell like <see cref="SetAttr"/>'s single-cell form) and restores bank
    /// 0 afterward, so a caller chaining a tile-index write right after this one lands in the right
    /// bank without doing its own bookkeeping.</summary>
    internal static void FillAttr(byte* mapBase, byte col, byte row, byte w, byte h, byte attr)
    {
        if (!Video.IsCgb)
            return;
        Cgb.SelectVramBank(1);
        for (byte r = 0; r < h; r++)
        for (byte c = 0; c < w; c++)
        {
            Ppu.WaitForVramAccess();
            *(mapBase + Index((byte)(col + c), (byte)(row + r))) = attr;
        }
        Cgb.SelectVramBank(0);
    }

    private static ushort Index(byte col, byte row) => (ushort)((ushort)row * 32 + col);
}
