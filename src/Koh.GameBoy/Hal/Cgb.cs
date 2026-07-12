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
        Hardware.KEY1 = 1;
        Hardware.Stop();
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
