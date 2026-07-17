using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// The window tile layer, map $9C00 (<see cref="Gb.TileMap1"/>) — graphics-library design doc §3
/// "Bg.cs / Win.cs": "same surface minus Clear". Every write here — including the single-cell case — goes
/// through the shared <see cref="MapWriter"/> against map 1 ($9C00), the same WRAM shadow + vblank flush
/// path <see cref="Bg"/> uses (see <see cref="MapWriter"/>'s remarks): a game never writes tilemap VRAM
/// directly, so the mode-3 hazard is not expressible from game code, and there is no timing call anywhere
/// on this surface. No <c>Clear</c>: unlike <see cref="Bg"/>, which every sample blanks up front to hide
/// scroll-wrapped stale cells, the window is only visible inside its own on-screen rect
/// (<see cref="Video.ShowWindow"/>) — nothing scrolls it into view, so there is no stale-cell case to guard
/// against. (<see cref="Video.Init"/> still clears map 1's shadow + VRAM so the flush of any later window
/// write draws from a fully-populated mirror.)
/// </summary>
public static unsafe class Win
{
    /// <summary>Set the tile index at (col, row). Routes through <see cref="MapWriter"/>'s shadow (direct
    /// to VRAM when the LCD is off, dirty-marked for the next vblank flush when on). No timing wait.</summary>
    public static void SetTile(byte col, byte row, byte tile) =>
        MapWriter.SetTile(1, col, row, tile);

    /// <summary>Paint a <paramref name="w"/> x <paramref name="h"/> rect of one tile index starting at
    /// (<paramref name="col"/>, <paramref name="row"/>).</summary>
    public static void Fill(byte col, byte row, byte w, byte h, byte tile) =>
        MapWriter.Fill(1, col, row, w, h, tile);

    /// <summary>Blit a row-major <paramref name="w"/> x <paramref name="h"/> rect of ROM tile indices
    /// from <paramref name="tiles"/> starting at (<paramref name="col"/>, <paramref name="row"/>).</summary>
    public static void DrawMap(byte col, byte row, byte w, byte h, byte[] tiles)
    {
        fixed (byte* source = &tiles[0])
            MapWriter.DrawMap(1, col, row, w, h, source);
    }

    /// <summary>CGB only: set one cell's per-tile attribute byte (VRAM bank 1). Silent no-op on DMG.
    /// Direct-to-VRAM; per-frame LCD-on attribute shadowing is out of scope (see <see cref="MapWriter"/>'s
    /// remarks).</summary>
    public static void SetAttr(byte col, byte row, byte attr) =>
        MapWriter.SetAttr(1, col, row, attr);
}
