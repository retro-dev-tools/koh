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

    public static int Subnormal() => Add(1, 1);

    public static int Transition() => Add(0x007FFFFF, 1);

    public static int TinyCancellation() => Add(0x00800000, 0x807FFFFF);

    public static int NegativeTiny() => Add(0x80000001, 0x80000001);

    public static int NegativeZero() => Add(0x80000000, 0x80000000);

    public static int OppositeZeros() => Add(0x80000000, 0);

    public static int Infinity() => Add(0x7F800000, 0x3F800000);

    public static int OppositeInfinities() => Add(0x7F800000, 0xFF800000);

    public static int QuietNan() => Add(0x7FC12345, 0x3F800000);

    public static int SignalingNan() => Add(0x7FA12345, 0x3F800000);

    public static int Overflow() => Add(0x7F7FFFFF, 0x7F7FFFFF);

    public static int HalfEvenLow() => Add(0x3F800000, 0x33800000);

    public static int HalfEvenHigh() => Add(0x3F800001, 0x33800000);

    public static int HalfBelowOne() => Add(0x3F800000, 0xB3000000);
}
