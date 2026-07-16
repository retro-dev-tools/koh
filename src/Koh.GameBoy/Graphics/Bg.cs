using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// The background tile layer, map $9800 (<see cref="Gb.TileMap"/>) — graphics-library design doc §3
/// "Bg.cs / Win.cs". Kills the nested 2x2-block loops every sample hand-rolls (<c>RenderBoard</c>) and,
/// on CGB, unwraps VRAM-bank-1 per-tile attributes for the first time. <see cref="SetTile"/> reuses the
/// Hal escape hatch (<see cref="Tilemap.SetTile"/>) where it already fits — this IS the $9800 map — with
/// this module adding only the immediate-checked gate; every bulk operation (<see cref="Fill"/>,
/// <see cref="DrawMap"/>, the CGB attribute writes) and <see cref="Win"/>'s identical surface both go
/// through the shared <see cref="MapWriter"/> instead, per the architecture rule of one shared
/// implementation for both maps.
/// </summary>
public static unsafe class Bg
{
    /// <summary>Set the tile index at (col, row). Immediate-checked (gates on
    /// <see cref="Ppu.WaitForVramAccess"/>) before delegating to <see cref="Tilemap.SetTile"/>.</summary>
    public static void SetTile(byte col, byte row, byte tile)
    {
        Ppu.WaitForVramAccess();
        Tilemap.SetTile(col, row, tile);
    }

    /// <summary>Paint a <paramref name="w"/> x <paramref name="h"/> rect of one tile index starting at
    /// (<paramref name="col"/>, <paramref name="row"/>) — the missing "paint an NxM block" from
    /// <c>RenderBoard</c>.</summary>
    public static void Fill(byte col, byte row, byte w, byte h, byte tile) =>
        MapWriter.Fill(Gb.TileMap, col, row, w, h, tile);

    /// <summary>Blit a row-major <paramref name="w"/> x <paramref name="h"/> rect of ROM tile indices
    /// from <paramref name="tiles"/> starting at (<paramref name="col"/>, <paramref name="row"/>).</summary>
    public static void DrawMap(byte col, byte row, byte w, byte h, byte[] tiles)
    {
        fixed (byte* source = &tiles[0])
            MapWriter.DrawMap(Gb.TileMap, col, row, w, h, source);
    }

    /// <summary>Set every cell of the full 32x32 map to one tile index (delegates to
    /// <see cref="Tilemap.Clear"/>).</summary>
    public static void Clear(byte tile) => Tilemap.Clear(tile);

    /// <summary>CGB only: set one cell's per-tile attribute byte (VRAM bank 1) — flip/priority/palette,
    /// see <see cref="TileAttr"/>. Silent no-op on DMG (no bank 1 to switch into).</summary>
    public static void SetAttr(byte col, byte row, byte attr) =>
        MapWriter.SetAttr(Gb.TileMap, col, row, attr);

    /// <summary>CGB only: set a <paramref name="w"/> x <paramref name="h"/> rect of one attribute byte.
    /// Silent no-op on DMG.</summary>
    public static void FillAttr(byte col, byte row, byte w, byte h, byte attr) =>
        MapWriter.FillAttr(Gb.TileMap, col, row, w, h, attr);
}
