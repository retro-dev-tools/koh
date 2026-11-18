namespace Koh.GameBoy;

/// <summary>The pixel-processing unit's timing: wait for vertical blank, the safe window to touch VRAM
/// and the tile map without tearing.</summary>
public static class Ppu
{
    /// <summary>SCX table at consecutive HBlanks, starting next frame.</summary>
    /// <remarks>LCD on; firstLine + count at most 144. Nonempty calls leave interrupts disabled.</remarks>
    [KohIntrinsic("hblankstream", Hardware.ScrollXAddress)]
    public static unsafe void StreamScrollX(byte* values, byte firstLine, byte count) =>
        throw new PlatformNotSupportedException();

    /// <summary>Next VBlank window. LCD-on precondition.</summary>
    public static void WaitVBlank()
    {
        while (Hardware.LY >= 144) { }
        while (Hardware.LY < 144) { }
    }

    public static void WaitForVramAccess()
    {
        while ((Hardware.STAT & 3) == 3) { }
    }

    public static void WaitForHBlank()
    {
        while ((Hardware.STAT & 3) == 0) { }
        while ((Hardware.STAT & 3) != 0) { }
    }
}
