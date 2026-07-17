using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// The background tile layer, map $9800 (<see cref="Gb.TileMap"/>) — graphics-library design doc §3
/// "Bg.cs / Win.cs". Kills the nested 2x2-block loops every sample hand-rolls (<c>RenderBoard</c>) and,
/// on CGB, unwraps VRAM-bank-1 per-tile attributes for the first time. Every tile-index write here routes
/// through the shared <see cref="MapWriter"/>'s WRAM shadow + vblank flush (see <see cref="MapWriter"/>'s
/// remarks): a game NEVER writes tilemap VRAM directly, so the mode-3 VRAM-write hazard is not expressible
/// from game code — no <see cref="Ppu.WaitForVramAccess"/> gate, no timing calls of any kind. The one
/// timing-sensitive routine is <see cref="MapWriter.Flush"/>, which <see cref="Video.EndFrame"/> runs in
/// vblank. <see cref="Win"/>'s identical surface targets the other map through the same shared writer.
/// </summary>
public static unsafe class Bg
{
    /// <summary>Set the tile index at (col, row). Routes through <see cref="MapWriter"/>'s shadow: written
    /// straight to VRAM when the LCD is off, otherwise mirrored in WRAM and flushed in the next vblank. No
    /// timing wait.</summary>
    public static void SetTile(byte col, byte row, byte tile) =>
        MapWriter.SetTile(0, col, row, tile);

    /// <summary>Paint a <paramref name="w"/> x <paramref name="h"/> rect of one tile index starting at
    /// (<paramref name="col"/>, <paramref name="row"/>) — the missing "paint an NxM block" from
    /// <c>RenderBoard</c>.</summary>
    public static void Fill(byte col, byte row, byte w, byte h, byte tile) =>
        MapWriter.Fill(0, col, row, w, h, tile);

    /// <summary>Blit a row-major <paramref name="w"/> x <paramref name="h"/> rect of ROM tile indices
    /// from <paramref name="tiles"/> starting at (<paramref name="col"/>, <paramref name="row"/>).</summary>
    public static void DrawMap(byte col, byte row, byte w, byte h, byte[] tiles)
    {
        fixed (byte* source = &tiles[0])
            MapWriter.DrawMap(0, col, row, w, h, source);
    }

    /// <summary>Set every cell of the full 32x32 map to one tile index (through the shadow, so the shadow
    /// and VRAM stay consistent — an LCD-off clear lands in VRAM immediately, an LCD-on clear flushes over
    /// the following vblanks).</summary>
    public static void Clear(byte tile) => MapWriter.Clear(0, tile);

    /// <summary>CGB only: set one cell's per-tile attribute byte (VRAM bank 1) — flip/priority/palette,
    /// see <see cref="TileAttr"/>. Silent no-op on DMG (no bank 1 to switch into). Direct-to-VRAM; per-frame
    /// LCD-on attribute shadowing is out of scope (see <see cref="MapWriter"/>'s remarks).</summary>
    public static void SetAttr(byte col, byte row, byte attr) =>
        MapWriter.SetAttr(0, col, row, attr);

    /// <summary>CGB only: set a <paramref name="w"/> x <paramref name="h"/> rect of one attribute byte.
    /// Silent no-op on DMG.</summary>
    public static void FillAttr(byte col, byte row, byte w, byte h, byte attr) =>
        MapWriter.FillAttr(0, col, row, w, h, attr);
}
