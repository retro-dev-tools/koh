// Art loading and CGB color. NO tile bytes live in source: the sheets are PNGs under art/, and
// the SDK's tile pipeline generates `Art` (2bpp tables + each sheet's actual colors as RGB555)
// at build time. Tile ids are fixed by the load order below; palettes are authored in the PNGs.
using Koh.GameBoy.Framework;
using Koh.GameBoy.Graphics;

namespace Koh.Samples.GbJrpg;

static class Assets
{
    // VRAM tile ids (load order below). Terrain sheets carry texture VARIANTS (grass 4, wall 2,
    // water 2) the renderer scatters; the tree is a 2x2 block from a 16x16 sheet (2 tiles across:
    // TL, TL+1 / TL+2, TL+3). Characters and monsters are 16x16 FIGURES — 2x2 tile blocks from
    // 4-tile-across sheets, so a figure's id is its TOP-LEFT tile: TR = +1, BL = +4, BR = +5
    // (see DrawFigure in Scenes.cs).
    public const byte Grass = 0; // 4 variants: 0 plain, 1-3 tufts/flowers
    public const byte Wall = 4; // 2 masonry variants
    public const byte Water = 6; // 2 wave variants
    public const byte Tree = 8; // 2x2 block: 8,9 / 10,11
    public const byte HeroTile = 12; // chars sheet figure 0 -> tiles 12,13 / 16,17
    public const byte NpcTile = 14; // chars sheet figure 1 -> tiles 14,15 / 18,19
    public const byte SlimeTile = 20; // monsters sheet figure 0 -> tiles 20,21 / 24,25
    public const byte BatTile = 22; // monsters sheet figure 1 -> tiles 22,23 / 26,27
    public const byte GhostTile = 28; // monsters sheet figure 2 -> tiles 28,29 / 32,33
    public const byte DrakeTile = 30; // monsters sheet figure 3 -> tiles 30,31 / 34,35

    // The UI window-frame sheet (10 tiles): corners/edges, flat fill, "more text" arrow.
    public const byte UiTopLeft = 36;
    public const byte UiTop = 37;
    public const byte UiTopRight = 38;
    public const byte UiLeft = 39;
    public const byte UiRight = 40;
    public const byte UiBottomLeft = 41;
    public const byte UiBottom = 42;
    public const byte UiBottomRight = 43;
    public const byte WindowFill = 44;
    public const byte MoreArrow = 45;

    // CGB background palette slots (of the hardware's 8).
    public const byte GrassPal = 0;
    public const byte WallPal = 1;
    public const byte WaterPal = 2;
    public const byte CharsPal = 3;
    public const byte MonsterPal = 4;
    public const byte TreePal = 5;
    public const byte UiPal = 6; // windows, HUD, battle backdrop — also the font's home

    public const byte CharsObjPal = 0; // CGB OBJECT palette slot for the hero sprite

    public static byte FontBase;

    /// <summary>A truly blank tile: the font's space glyph (tile 0 is grass ART, not blank).</summary>
    public static byte Blank => FontBase;

    public static void Load()
    {
        var grass = TileAsset.Define(Art.GrassTiles);
        var wall = TileAsset.Define(Art.WallTiles);
        var water = TileAsset.Define(Art.WaterTiles);
        var tree = TileAsset.Define(Art.TreeTiles);
        var chars = TileAsset.Define(Art.CharsTiles);
        var monsters = TileAsset.Define(Art.MonstersTiles);
        var ui = TileAsset.Define(Art.UiTiles);

        grass.Load(Grass);
        wall.Load(Wall);
        water.Load(Water);
        tree.Load(Tree);
        chars.Load(HeroTile);
        monsters.Load(SlimeTile);
        ui.Load(UiTopLeft);

        FontBase = (byte)(UiTopLeft + ui.TileCount);
        Font.LoadDefault(FontBase);

        // Each sheet's own colors become its CGB palette (this is a CGB-exclusive ROM — the
        // dmgShades byte is moot but required by the dual-authoring API).
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
        Palettes.SetBg(
            TreePal,
            Art.TreeColor0,
            Art.TreeColor1,
            Art.TreeColor2,
            Art.TreeColor3,
            0xE4
        );
        Palettes.SetBg(UiPal, Art.UiColor0, Art.UiColor1, Art.UiColor2, Art.UiColor3, 0xE4);

        // The hero's 16x16 figure is drawn as hardware OBJs, not BG tiles (BG tile rewrites drip
        // through MapWriter's vblank flush and ghost across several frames on a move; sprites are
        // an O(1) OAM write). OBJ color 0 is transparent — the sheet's grass-green background
        // disappears around the figure — colors 1-3 match the BG chars palette so the hero looks
        // identical whether it's ever drawn via Scenery.DrawFigure (battle layouts) or Sprites.
        Palettes.SetObj(
            CharsObjPal,
            Art.CharsColor0,
            Art.CharsColor1,
            Art.CharsColor2,
            Art.CharsColor3,
            0xE4
        );
    }
}
