using Koh.GameBoy;

public static class Program
{
    static int Add(uint left, uint right)
    {
        Hardware.SCY = 0;
        Hardware.SCX = 0;
        Hardware.BGP = 0;
        Hardware.WY = 0;
        uint result = SoftFloat.AddF32(left, right);
        Hardware.SCX = 1;
        Hardware.SCY = 0xEE;
        return (int)result;
    }

    public static int Main() => Add(0x3FC00000, 0x40200000);

    public static int Rounding() => Add(0x3DCCCCCD, 0x3E4CCCCD);

    public static int Opposite() => Add(0xC0200000, 0x3F400000);

    public static int Cancellation() => Add(0x3F800000, 0xBF800000);

    public static int ExponentGap() => Add(0x42C80000, 0x3F000000);

    public static int Sticky() => Add(0x49742400, 0x3E800000);
}
