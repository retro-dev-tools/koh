namespace Koh.GameBoy;

/// <summary>The pixel-processing unit's timing: wait for vertical blank, the safe window to touch VRAM
/// and the tile map without tearing.</summary>
public static class Ppu
{
    /// <summary>Spin until the LCD next enters vertical blank (scanline 144).</summary>
    public static void WaitVBlank()
    {
        while (Hardware.LY == 144) { } // leave the current vblank, if in one
        while (Hardware.LY != 144) { } // wait for the next one
    }
}
