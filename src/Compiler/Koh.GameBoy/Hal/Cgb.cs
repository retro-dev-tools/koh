namespace Koh.GameBoy;

public static unsafe class Cgb
{
    public static bool IsColor()
    {
        return Hardware.KEY1 != 0xFF;
    }

    public static bool TryEnableDoubleSpeed()
    {
        if (!IsColor())
            return false;
        if ((Hardware.KEY1 & 0x80) != 0)
            return true;

        // Pan Docs' canonical speed-switch sequence: a pending/firing interrupt right at STOP can
        // derail the switch on real hardware, so mask interrupts, deselect the joypad matrix
        // (P1/JOYP = $30, per Pan Docs) and clear IF before arming KEY1 and executing STOP, then
        // restore interrupts once the switch has resolved.
        Hardware.DisableInterrupts();
        Hardware.JOYP = 0x30;
        Hardware.IF = 0;
        Hardware.KEY1 = 1;
        Hardware.Stop();
        Hardware.EnableInterrupts();
        return (Hardware.KEY1 & 0x80) != 0;
    }

    public static void SelectVramBank(byte bank)
    {
        if (IsColor())
            Hardware.VBK = (byte)(bank & 1);
    }

    /// <summary>Copy <paramref name="byteCount"/> bytes from ROM/WRAM to VRAM with the CGB's
    /// general-purpose DMA (HDMA1-5 with bit 7 of HDMA5 clear): 2 bytes per M-cycle with the CPU
    /// halted, so 1920 bytes cost ~480 M-cycles — comfortably inside one vblank. Call only during
    /// vblank (after <see cref="Ppu.WaitVBlank"/>): the engine writes blindly, and blocks that
    /// collide with PPU mode 3 are dropped on real hardware (Pan Docs). Hardware-imposed
    /// constraints: <paramref name="source"/> and <paramref name="vramDest"/> are 16-byte aligned
    /// (the registers ignore the low 4 bits), and <paramref name="byteCount"/> is a multiple of 16,
    /// at most 2048 (128 blocks per transfer). A <paramref name="byteCount"/> of 0 is a no-op —
    /// without this guard, <c>(0 &gt;&gt; 4) - 1</c> underflows to 0xFF, whose bit 7 being SET selects
    /// HBlank-DMA mode (not general-purpose) with a 0x7F length field (128 blocks = 2048 bytes),
    /// silently kicking off an unwanted transfer instead of doing nothing. No-op on DMG, where these
    /// registers don't exist — callers keep a CPU-copy fallback behind <see cref="IsColor"/>.</summary>
    public static void CopyToVram(byte* source, ushort vramDest, ushort byteCount)
    {
        if (!IsColor() || byteCount == 0)
            return;
        ushort src = (ushort)source;
        Hardware.HDMA1 = (byte)(src >> 8);
        Hardware.HDMA2 = (byte)src;
        Hardware.HDMA3 = (byte)(vramDest >> 8);
        Hardware.HDMA4 = (byte)vramDest;
        // (blocks - 1) with bit 7 clear = general-purpose mode; the CPU stalls until it completes.
        Hardware.HDMA5 = (byte)((byteCount >> 4) - 1);
    }

    public static void SetBackgroundColor(byte palette, byte color, ushort rgb555)
    {
        if (!IsColor())
            return;
        byte index = (byte)(((palette & 7) * 8 + (color & 3) * 2) & 0x3F);
        Hardware.BCPS = index;
        Hardware.BCPD = (byte)rgb555;
        Hardware.BCPS = (byte)(index + 1);
        Hardware.BCPD = (byte)(rgb555 >> 8);
    }
}
