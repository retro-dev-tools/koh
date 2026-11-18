namespace Koh.GameBoy;

public static unsafe class Cgb
{
    public static bool IsColor()
    {
        if (Hardware.KEY1 != 0xFF)
            return true;
        Hardware.KEY1 = 0;
        if (Hardware.KEY1 == 0xFF)
            return false;
        Hardware.KEY1 = 1;
        return true;
    }

    /// <summary>Startup speed switch; call with IME disabled. Preserves IE and leaves IME disabled.</summary>
    public static bool TryEnableDoubleSpeed()
    {
        if (!IsColor())
            return false;
        if ((Hardware.KEY1 & 0x80) != 0)
            return true;

        Hardware.DisableInterrupts();
        byte enabled = Hardware.IE;
        Hardware.IE = 0;
        Hardware.JOYP = 0x30;
        Hardware.KEY1 = 1;
        Hardware.Stop();
        Hardware.IE = enabled;
        return (Hardware.KEY1 & 0x80) != 0;
    }

    public static void SelectVramBank(byte bank)
    {
        if (IsColor())
            Hardware.VBK = (byte)(bank & 1);
    }

    /// <summary>Copy <paramref name="byteCount"/> bytes from ROM/WRAM to VRAM with the CGB's
    /// general-purpose DMA (HDMA1-5 with bit 7 of HDMA5 clear): 2 bytes per M-cycle with the CPU
    /// halted. LCD-on copies use at most 1024 bytes per VBlank. Blocks that
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
        if ((Hardware.LCDC & 0x80) != 0 && byteCount > 1024)
        {
            CopyChunk(source, vramDest, 1024);
            CopyChunk(source + 1024, (ushort)(vramDest + 1024), (ushort)(byteCount - 1024));
        }
        else
            CopyChunk(source, vramDest, byteCount);
    }

    private static void CopyChunk(byte* source, ushort vramDest, ushort byteCount)
    {
        ushort src = (ushort)source;
        Hardware.HDMA1 = (byte)(src >> 8);
        Hardware.HDMA2 = (byte)src;
        Hardware.HDMA3 = (byte)(vramDest >> 8);
        Hardware.HDMA4 = (byte)vramDest;
        byte control = (byte)((byteCount >> 4) - 1);
        if ((Hardware.LCDC & 0x80) != 0)
            Ppu.WaitVBlank();
        Hardware.HDMA5 = control;
    }

    /// <remarks>IME disabled after CGB writes.</remarks>
    public static void SetBackgroundColor(byte palette, byte color, ushort rgb555)
    {
        if (!IsColor())
            return;
        byte index = (byte)(((palette & 7) * 8 + (color & 3) * 2) & 0x3F);
        WriteBackgroundColor(index, rgb555);
    }

    internal static void SetObjectColor(byte palette, byte color, ushort rgb555)
    {
        if (!IsColor())
            return;
        byte index = (byte)(((palette & 7) * 8 + (color & 3) * 2) & 0x3F);
        WriteObjectColor(index, rgb555);
    }

    [KohIntrinsic("palettewrite", 0xFF69)]
    private static void WriteBackgroundColor(byte index, ushort rgb555)
    {
        Hardware.BCPS = index;
        Hardware.BCPD = (byte)rgb555;
        Hardware.BCPS = (byte)(index + 1);
        Hardware.BCPD = (byte)(rgb555 >> 8);
    }

    [KohIntrinsic("palettewrite", 0xFF6B)]
    private static void WriteObjectColor(byte index, ushort rgb555)
    {
        Hardware.OCPS = index;
        Hardware.OCPD = (byte)rgb555;
        Hardware.OCPS = (byte)(index + 1);
        Hardware.OCPD = (byte)(rgb555 >> 8);
    }
}
