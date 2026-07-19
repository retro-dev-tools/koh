// Art loading and CGB color. NO tile bytes live in source: the sheets are PNGs under art/, and
// the SDK's tile pipeline generates `Art` (2bpp tables + each sheet's actual colors as RGB555)
// at build time. Tile ids are fixed by the load order below; palettes are authored in the PNGs.
using Koh.GameBoy.Framework;
using Koh.GameBoy.Graphics;

namespace Koh.Samples.GbJrpg;

static class Assets
{
    // VRAM tile ids (load order below); terrain ids double as World.Map cell values. Characters
    // and monsters are 16x16 FIGURES — 2x2 tile blocks from 32x16 sheets (4 tiles across), so a
    // figure's id is its TOP-LEFT tile: TR = +1, BL = +4, BR = +5 (see DrawFigure in Scenes.cs).
    public const byte Grass = 0;
    public const byte Wall = 1;
    public const byte Water = 2;
    public const byte HeroTile = 3; // chars sheet figure 0 -> tiles 3,4 / 7,8
    public const byte NpcTile = 5; // chars sheet figure 1 -> tiles 5,6 / 9,10
    public const byte SlimeTile = 11; // monsters sheet figure 0 -> tiles 11,12 / 15,16
    public const byte BatTile = 13; // monsters sheet figure 1 -> tiles 13,14 / 17,18

    // CGB background palette slots (of the hardware's 8).
    public const byte GrassPal = 0; // also the UI/font palette
    public const byte WallPal = 1;
    public const byte WaterPal = 2;
    public const byte CharsPal = 3;
    public const byte MonsterPal = 4;

    public static byte FontBase;

    /// <summary>A truly blank tile: the font's space glyph (tile 0 is grass ART, not blank).</summary>
    public static byte Blank => FontBase;

    public static void Load()
    {
        var grass = TileAsset.Define(Art.GrassTiles);
        var wall = TileAsset.Define(Art.WallTiles);
        var water = TileAsset.Define(Art.WaterTiles);
        var chars = TileAsset.Define(Art.CharsTiles);
        var monsters = TileAsset.Define(Art.MonstersTiles);

        grass.Load(Grass);
        wall.Load(Wall);
        water.Load(Water);
        chars.Load(HeroTile);
        monsters.Load(SlimeTile);

        FontBase = (byte)(SlimeTile + monsters.TileCount);
        Font.LoadDefault(FontBase);

        // Each sheet's own colors become its CGB palette; the dmgShades byte keeps the same ROM
        // playable (shaded) on a monochrome Game Boy.
        Palettes.SetBg(
            GrassPal,
            Art.GrassColor0,
            Art.GrassColor1,
            Art.GrassColor2,
            Art.GrassColor3,
            0xE4
        );
        Palettes.SetBg(
            WallPal,
            Art.WallColor0,
            Art.WallColor1,
            Art.WallColor2,
            Art.WallColor3,
            0xE4
        );
        Palettes.SetBg(
            WaterPal,
            Art.WaterColor0,
            Art.WaterColor1,
            Art.WaterColor2,
            Art.WaterColor3,
            0xE4
        );
        Palettes.SetBg(
            CharsPal,
            Art.CharsColor0,
            Art.CharsColor1,
            Art.CharsColor2,
            Art.CharsColor3,
            0xE4
        );
        Palettes.SetBg(
            MonsterPal,
            Art.MonstersColor0,
            Art.MonstersColor1,
            Art.MonstersColor2,
            Art.MonstersColor3,
            0xE4
        );
    }

    /// <summary>The CGB palette a terrain cell colors with.</summary>
    public static byte PaletteFor(byte terrainTile) =>
        terrainTile == Wall ? WallPal
        : terrainTile == Water ? WaterPal
        : GrassPal;
}
