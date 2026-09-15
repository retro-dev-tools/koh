// The overworld: a 10x8 grid of 16x16 cells (each drawn as a 2x2 block of 8x8 tiles — characters
// are full 16x16 figures, so the world walks in figure-sized steps), stored as the natural C#
// shape — a rectangular byte[,] — plus collision and the encounter roll.
using Koh.GameBoy.Framework;

namespace Koh.Samples.GbJrpg;

static class World
{
    public const int Width = 10;
    public const int Height = 8;

    // Terrain CELL values (what the map means, decoupled from VRAM tile ids — the renderer's
    // DrawCell maps a cell to art, scattering texture variants).
    public const byte GrassCell = 0;
    public const byte WallCell = 1;
    public const byte WaterCell = 2;
    public const byte TreeCell = 3;

    // Millmere — the pond clearing where the old mill wall crumbled, at the edge of the west
    // wood: a lake with an organic SE shore, a ruined wall fragment mid-field, tree copses, and
    // a dead-end nook behind the west wood. 57/80 cells walkable, all reachable from the start.
    private static readonly byte[,] Map =
    {
        { 0, 0, 0, 0, 0, 0, 3, 3, 0, 0 },
        { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        { 3, 0, 0, 0, 0, 0, 0, 0, 2, 2 },
        { 3, 3, 0, 0, 1, 1, 0, 2, 2, 2 },
        { 0, 0, 3, 0, 1, 0, 0, 2, 2, 2 },
        { 0, 0, 3, 0, 0, 0, 0, 0, 2, 2 },
        { 0, 0, 3, 0, 0, 0, 0, 0, 0, 2 },
        { 0, 0, 0, 0, 0, 0, 3, 0, 0, 0 },
    };

    public static byte TileAt(int x, int y) => Map[y, x];

    public static bool IsWalkable(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            return false;
        return Map[y, x] == GrassCell;
    }

    /// <summary>One random-encounter roll per completed step: ~1 in 16.</summary>
    public static bool RollEncounter() => Rng.Chance(16);
}
