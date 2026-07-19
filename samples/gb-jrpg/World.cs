// The overworld: a 10x8 grid of 16x16 cells (each drawn as a 2x2 block of 8x8 tiles — characters
// are full 16x16 figures, so the world walks in figure-sized steps), stored as the natural C#
// shape — a rectangular byte[,] — plus collision and the encounter roll.
using Koh.GameBoy.Framework;

namespace Koh.Samples.GbJrpg;

static class World
{
    public const int Width = 10;
    public const int Height = 8;

    // Cell values are terrain tile ids: 1 = wall, 2 = water, 0 = grass.
    private static readonly byte[,] Map =
    {
        { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
        { 1, 0, 0, 0, 0, 0, 0, 0, 2, 1 },
        { 1, 0, 1, 0, 0, 0, 0, 0, 2, 1 },
        { 1, 0, 0, 0, 0, 2, 2, 0, 0, 1 },
        { 1, 0, 0, 0, 0, 2, 2, 0, 0, 1 },
        { 1, 0, 1, 1, 0, 0, 0, 0, 0, 1 },
        { 1, 0, 0, 0, 0, 0, 0, 0, 0, 1 },
        { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 },
    };

    public static byte TileAt(int x, int y) => Map[y, x];

    public static bool IsWalkable(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            return false;
        return Map[y, x] == Assets.Grass;
    }

    /// <summary>One random-encounter roll per completed step: ~1 in 16.</summary>
    public static bool RollEncounter() => Rng.Chance(16);
}
