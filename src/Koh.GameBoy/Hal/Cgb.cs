namespace Koh.GameBoy;

public static class Cgb
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
