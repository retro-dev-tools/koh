using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// Display lifecycle and the frame loop — the boot choreography every sample hand-orders today
/// (<c>Lcd.Off</c> -&gt; author -&gt; <c>Lcd.On</c>) plus layer toggles, scroll, and the one call that
/// paces a frame. Build ON the Hal primitives (<see cref="Ppu"/>/<see cref="Lcd"/>/<see cref="Cgb"/>/
/// <see cref="Tilemap"/>): Video never spins on STAT/LY itself, and goes to <see cref="Gb"/> pointers via
/// <see cref="Mem"/> for bulk clears rather than open-coding a loop here (see the remarks on
/// <see cref="Init"/> for why).
///
/// Shape: <c>Init()</c> (screen off, everything cleared/defaulted) -&gt; author (TileSet/Bg/Palettes/...,
/// not yet built) -&gt; <c>Start()</c> (screen on) -&gt; <c>while (true) { ...; EndFrame(); }</c>. No
/// callback-based game loop — a plain loop with one required call is what every sample already does.
/// </summary>
public static unsafe class Video
{
    // ---- Grayscale RGB555 shades used as the CGB boot default (slot 0 only; the Palettes module
    // gives games full control). white(31,31,31)/light(21,21,21)/dark(10,10,10)/black(0,0,0), each
    // packed r | g&lt;&lt;5 | b&lt;&lt;10.
    private const ushort GrayWhite = 0x7FFF;
    private const ushort GrayLight = 0x56B5;
    private const ushort GrayDark = 0x294A;
    private const ushort GrayBlack = 0x0000;

    /// <summary>Cached <see cref="Cgb.IsColor"/>, set once by <see cref="Init"/> so every other module
    /// branches on a WRAM read instead of re-deriving the KEY1 check.</summary>
    public static bool IsCgb;

    /// <summary>Free-running frame counter, ticked by <see cref="EndFrame"/> (animation timing / a
    /// cheap RNG seed). Wraps 255 -&gt; 0 like any other byte counter.</summary>
    public static byte FrameCount;

    /// <summary>Mirror of the LCDC bits Video owns (layers + the screen-on bit), so
    /// <see cref="ShowSprites"/>/<see cref="HideSprites"/>/<see cref="ShowWindow"/>/
    /// <see cref="HideWindow"/> compose correctly regardless of call order or whether the screen is on
    /// yet — each one only flips its own bits in the mirror, then writes the whole mirror through.</summary>
    private static byte _lcdcMirror;

    /// <summary>LCD off, both tile maps and OAM cleared, all sprites hidden (OAM zeroed -&gt; Y=0 -&gt;
    /// off-screen), default palettes (DMG 0xE4 flat shades; CGB slot-0 grayscale), CGB detected and
    /// cached. The screen stays off — call <see cref="Start"/> once authoring is done.
    ///
    /// The two bulk clears go through <see cref="Mem.Fill"/> rather than a loop written here: this
    /// method already calls <see cref="Tilemap.Clear"/> (which loops internally), and
    /// <c>Mem.Copy</c>/<c>Mem.Fill</c>'s own doc remarks warn that two textually distinct stride-1
    /// pointer-walk loops in the SAME function corrupt each other under the SM83 backend's register
    /// allocator — so every additional bulk clear here reuses an already-tuned, single-loop helper
    /// instead of adding another loop body to this one.</summary>
    public static void Init()
    {
        Lcd.Off();
        IsCgb = Cgb.IsColor();

        Tilemap.Clear(0);
        Mem.Fill(Gb.TileMap1, 0, 1024); // window map ($9C00), 32x32 tiles
        Mem.Fill(Gb.Oam, 0, 160); // all 40 sprites: Y=0 hides every one

        _lcdcMirror = 0x11; // BG enable (bit0) + BG/window tile data at $8000 (bit4); screen stays off
        ApplyLcdc();

        Hardware.BGP = 0xE4;
        Hardware.OBP0 = 0xE4;
        Hardware.OBP1 = 0xE4;
        if (IsCgb)
            SetGrayscalePalettes();

        FrameCount = 0;
    }

    /// <summary>LCD on: background enabled, tile data at $8000, plus whatever layers were configured
    /// via <see cref="ShowSprites"/>/<see cref="ShowWindow"/> before this call.</summary>
    public static void Start()
    {
        _lcdcMirror |= 0x80;
        ApplyLcdc();
    }

    /// <summary>Vblank-safe LCD off (wraps <see cref="Lcd.Off"/>). Clears the screen-on bit from the
    /// layer mirror so a later <see cref="Start"/> re-enables cleanly with the same layer config.</summary>
    public static void Stop()
    {
        Lcd.Off();
        _lcdcMirror &= 0x7F;
    }

    /// <summary>Enable the sprite layer (LCDC bits 1+2: OBJ enable and size).</summary>
    public static void ShowSprites(SpriteSize size)
    {
        _lcdcMirror = (byte)((_lcdcMirror & ~0x06) | 0x02 | ((byte)size << 2));
        ApplyLcdc();
    }

    /// <summary>Disable the sprite layer (LCDC bit 1).</summary>
    public static void HideSprites()
    {
        _lcdcMirror = (byte)(_lcdcMirror & ~0x02);
        ApplyLcdc();
    }

    /// <summary>Enable the window layer at (x, y) in SCREEN pixels (LCDC bits 5+6; window map is
    /// always $9C00). Adds hardware's WX +7 offset internally.</summary>
    public static void ShowWindow(byte x, byte y)
    {
        Hardware.WX = (byte)(x + 7);
        Hardware.WY = y;
        _lcdcMirror = (byte)(_lcdcMirror | 0x60);
        ApplyLcdc();
    }

    /// <summary>Disable the window layer (LCDC bit 5).</summary>
    public static void HideWindow()
    {
        _lcdcMirror = (byte)(_lcdcMirror & ~0x20);
        ApplyLcdc();
    }

    /// <summary>Scroll the background to (x, y) (wraps <see cref="Lcd.Scroll"/>).</summary>
    public static void Scroll(byte x, byte y) => Lcd.Scroll(x, y);

    /// <summary>THE frame call: wait for vertical blank, flush pending OAM/palette writes (hooks for
    /// the Sprites/Palettes modules — later slices), then advance <see cref="FrameCount"/>.</summary>
    public static void EndFrame()
    {
        Ppu.WaitVBlank();
        // Sprite shadow-OAM flush (Hardware.RunOamDma on the dirty shadow) and pending CGB palette
        // writes land here once the Sprites/Palettes modules exist. No-op until then.
        FrameCount++;
    }

    private static void ApplyLcdc() => Hardware.LCDC = _lcdcMirror;

    private static void SetGrayscalePalettes()
    {
        Cgb.SetBackgroundColor(0, 0, GrayWhite);
        Cgb.SetBackgroundColor(0, 1, GrayLight);
        Cgb.SetBackgroundColor(0, 2, GrayDark);
        Cgb.SetBackgroundColor(0, 3, GrayBlack);
        SetObjectColor(0, 0, GrayWhite);
        SetObjectColor(0, 1, GrayLight);
        SetObjectColor(0, 2, GrayDark);
        SetObjectColor(0, 3, GrayBlack);
    }

    /// <summary>Same OCPS/OCPD index-and-write-twice protocol as <see cref="Cgb.SetBackgroundColor"/>,
    /// targeting object palette RAM instead of background palette RAM (Cgb.cs has no OBJ variant).</summary>
    private static void SetObjectColor(byte palette, byte color, ushort rgb555)
    {
        byte index = (byte)(((palette & 7) * 8 + (color & 3) * 2) & 0x3F);
        Hardware.OCPS = index;
        Hardware.OCPD = (byte)rgb555;
        Hardware.OCPS = (byte)(index + 1);
        Hardware.OCPD = (byte)(rgb555 >> 8);
    }
}

/// <summary>Sprite dimensions for LCDC bit 2, as configured via <see cref="Video.ShowSprites"/>.</summary>
public enum SpriteSize : byte
{
    Size8x8 = 0,
    Size8x16 = 1,
}
