using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// The window tile layer, map $9C00 (<see cref="Gb.TileMap1"/>) — graphics-library design doc §3
/// "Bg.cs / Win.cs": "same surface minus Clear". There is no Hal helper for $9C00 (<see cref="Tilemap"/>
/// only targets $9800), so every write here — including the single-cell case — goes through the shared
/// <see cref="MapWriter"/> against <see cref="Gb.TileMap1"/> directly. No <c>Clear</c>: unlike
/// <see cref="Bg"/>, which every sample blanks up front to hide scroll-wrapped stale cells, the window is
/// only visible inside its own on-screen rect (<see cref="Video.ShowWindow"/>) — nothing scrolls it into
/// view, so there is no stale-cell case to guard against.
/// </summary>
public static unsafe class Win
{
    /// <summary>Set the tile index at (col, row). Immediate-checked.</summary>
    public static void SetTile(byte col, byte row, byte tile) =>
        MapWriter.SetTile(Gb.TileMap1, col, row, tile);

    /// <summary>Paint a <paramref name="w"/> x <paramref name="h"/> rect of one tile index starting at
    /// (<paramref name="col"/>, <paramref name="row"/>).</summary>
    public static void Fill(byte col, byte row, byte w, byte h, byte tile) =>
        MapWriter.Fill(Gb.TileMap1, col, row, w, h, tile);

    /// <summary>Blit a row-major <paramref name="w"/> x <paramref name="h"/> rect of ROM tile indices
    /// from <paramref name="tiles"/> starting at (<paramref name="col"/>, <paramref name="row"/>).</summary>
    public static void DrawMap(byte col, byte row, byte w, byte h, byte[] tiles)
    {
        fixed (byte* source = &tiles[0])
            MapWriter.DrawMap(Gb.TileMap1, col, row, w, h, source);
    }

    /// <summary>CGB only: set one cell's per-tile attribute byte (VRAM bank 1). Silent no-op on DMG.</summary>
    public static void SetAttr(byte col, byte row, byte attr) =>
        MapWriter.SetAttr(Gb.TileMap1, col, row, attr);
}
