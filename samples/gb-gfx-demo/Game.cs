// The graphics-library showcase: one ROM exercising every module under Koh.GameBoy.Graphics
// (docs/superpowers/specs/2026-07-15-graphics-library-design.md), so this file is also the reference
// example for how a game/demo writer uses the library. Everything below reads high-level — no hardware
// register, VRAM address, or OAM byte offset appears in this file; that is exactly the point.
//
// The generic Game Boy surface (Hardware/Gb/Mem, Hal) is brought into scope by the Koh SDK's global
// <Using> (Koh.GameBoy); this file adds the one Graphics using the SDK doesn't inject automatically.
using Koh.GameBoy.Graphics;

namespace Koh.Samples.GbGfxDemo;

static class Game
{
    // ---- Tile art -----------------------------------------------------------------------------

    // Two solid-color checkerboard tiles, 2bpp, 16 bytes each (row = low-plane byte, high-plane byte).
    // Tile 0 = color 1 (light), tile 1 = color 2 (dark) — see TileSet.Load's remarks for why loading
    // takes an explicit tile count instead of measuring this array's own length.
    private static readonly byte[] CheckerTiles =
    {
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00, // tile 0: color 1
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF,
        0x00,
        0xFF, // tile 1: color 2
    };

    // A single filled-diamond sprite tile (color 3 throughout its shape), 2bpp, 16 bytes.
    private static readonly byte[] OrbTile =
    {
        0x18,
        0x18,
        0x3C,
        0x3C,
        0x7E,
        0x7E,
        0xFF,
        0xFF,
        0xFF,
        0xFF,
        0x7E,
        0x7E,
        0x3C,
        0x3C,
        0x18,
        0x18,
    };

    private const byte CheckerTile0 = 0;
    private const byte OrbSpriteTile = 2;
    private const byte FontFirstTile = 16; // leaves 3..15 free, well clear of the 96-glyph font table

    // ---- Orbit path -----------------------------------------------------------------------------

    // A 16-point circle of radius 24, one point every 22.5 degrees, so four sprites 90 degrees apart
    // (four table steps) trace the same circle without any trig at runtime.
    private static readonly sbyte[] OrbitDx =
    {
        24,
        22,
        17,
        9,
        0,
        -9,
        -17,
        -22,
        -24,
        -22,
        -17,
        -9,
        0,
        9,
        17,
        22,
    };
    private static readonly sbyte[] OrbitDy =
    {
        0,
        9,
        17,
        22,
        24,
        22,
        17,
        9,
        0,
        -9,
        -17,
        -22,
        -24,
        -22,
        -17,
        -9,
    };
    private const int OrbitCenterX = 80;
    private const int OrbitCenterY = 72;
    private const byte OrbitSteps = 16;
    private const byte PaletteChangeFrames = 60;

    static void Main()
    {
        Video.Init(); // LCD off; both tilemaps + OAM cleared; CGB detected (Video.IsCgb)

        TileSet.Load(CheckerTile0, CheckerTiles, 2);
        TileSet.Load(OrbSpriteTile, OrbTile, 1);
        Font.LoadDefault(FontFirstTile);

        // Scrolling checkerboard: paint the full 32x32 background map so scrolling wraps seamlessly.
        for (byte row = 0; row < 32; row++)
        for (byte col = 0; col < 32; col++)
            Bg.SetTile(col, row, (byte)(((row + col) & 1) == 0 ? CheckerTile0 : CheckerTile0 + 1));

        // Window HUD: a one-line "SCORE 01234" strip pinned near the bottom of the screen.
        Video.ShowWindow(0, 128);
        Text.DrawToWindow(0, 0, "SCORE ");

        // Four orbiting sprites, each a quarter-turn apart on the orbit table, with a different flip
        // attribute so all four ObjAttr flip combinations show on screen at once.
        Sprite orb0;
        Sprite orb1;
        Sprite orb2;
        Sprite orb3;
        Sprites.Get(0, out orb0);
        Sprites.Get(1, out orb1);
        Sprites.Get(2, out orb2);
        Sprites.Get(3, out orb3);
        orb0.SetTile(OrbSpriteTile);
        orb1.SetTile(OrbSpriteTile);
        orb2.SetTile(OrbSpriteTile);
        orb3.SetTile(OrbSpriteTile);
        orb0.SetAttr(0);
        orb1.SetAttr(ObjAttr.FlipX);
        orb2.SetAttr(ObjAttr.FlipY);
        orb3.SetAttr((byte)(ObjAttr.FlipX | ObjAttr.FlipY));

        Video.ShowSprites(SpriteSize.Size8x8);
        Video.Start(); // LCD on

        byte orbitStep = 0;
        byte scrollX = 0;
        ushort score = 0;
        bool altPalette = false;

        while (true)
        {
            // Palette change every PaletteChangeFrames frames — the same call dispatches to CGB RGB555
            // palette RAM or DMG BGP/OBP shades depending on the hardware this ROM is running on.
            if (Video.FrameCount % PaletteChangeFrames == 0)
            {
                ApplyPalette(altPalette);
                altPalette = !altPalette;
            }

            Video.Scroll(scrollX, 0);
            scrollX++;

            byte i0 = orbitStep;
            byte i1 = (byte)((orbitStep + OrbitSteps / 4) % OrbitSteps);
            byte i2 = (byte)((orbitStep + OrbitSteps / 2) % OrbitSteps);
            byte i3 = (byte)((orbitStep + 3 * OrbitSteps / 4) % OrbitSteps);
            orb0.Move(OrbitCenterX + OrbitDx[i0], OrbitCenterY + OrbitDy[i0]);
            orb1.Move(OrbitCenterX + OrbitDx[i1], OrbitCenterY + OrbitDy[i1]);
            orb2.Move(OrbitCenterX + OrbitDx[i2], OrbitCenterY + OrbitDy[i2]);
            orb3.Move(OrbitCenterX + OrbitDx[i3], OrbitCenterY + OrbitDy[i3]);
            orbitStep = (byte)((orbitStep + 1) % OrbitSteps);

            score += 17;
            Text.DrawNumberToWindow(6, 0, score, 5);

            Video.EndFrame(); // vblank + OAM flush, one call
        }
    }

    private static void ApplyPalette(bool alt)
    {
        if (!alt)
        {
            Palettes.SetBg(0, Rgb.White, Rgb.Make(20, 25, 20), Rgb.Make(8, 14, 8), Rgb.Black, 0xE4);
            Palettes.SetObj(0, 0, Rgb.White, Rgb.Make(31, 0, 0), Rgb.Black, 0x1C);
        }
        else
        {
            Palettes.SetBg(0, Rgb.White, Rgb.Make(0, 20, 25), Rgb.Make(0, 8, 14), Rgb.Black, 0x93);
            Palettes.SetObj(0, 0, Rgb.Yellow, Rgb.Blue, Rgb.Black, 0xE0);
        }
    }
}
