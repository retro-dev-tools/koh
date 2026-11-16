using Koh.GameBoy;

public static class Program
{
    static byte Fib(byte n)
    {
        if (n < 2)
            return n;
        return (byte)(Fib((byte)(n - 1)) + Fib((byte)(n - 2)));
    }

    static bool IsEven(byte n)
    {
        if (n == 0)
            return true;
        return IsOdd((byte)(n - 1));
    }

    static bool IsOdd(byte n)
    {
        if (n == 0)
            return false;
        return IsEven((byte)(n - 1));
    }

    static byte Sum(int n)
    {
        if (n == 0)
            return 0;
        byte x = (byte)n;
        return (byte)(Sum(n - 1) + x);
    }

    static sbyte SignedSum(int n)
    {
        if (n == 0)
            return -7;
        sbyte x = (sbyte)(n * 3);
        return (sbyte)(SignedSum(n - 1) + x);
    }

    static ushort Sum16(int n)
    {
        if (n == 0)
            return 60000;
        ushort x = (ushort)n;
        return (ushort)(Sum16(n - 1) + x);
    }

    static int Sum32(int n)
    {
        if (n == 0)
            return 0x7ffffff0;
        return Sum32(n - 1) + n;
    }

    static long Sum64(int n)
    {
        if (n == 0)
            return 0x7ffffffffffffff0L;
        long x = n;
        return x + Sum64(n - 1);
    }

    static void Prepare()
    {
        Hardware.SCY = 0;
        Hardware.SCX = 0;
        Hardware.BGP = 0;
        Hardware.WY = 0;
    }

    public static int Fibonacci()
    {
        Prepare();
        byte value = Fib(10);
        Hardware.SCX = value;
        return value;
    }

    public static int Mutual()
    {
        Prepare();
        byte even = IsEven(10) ? (byte)1 : (byte)0;
        byte odd = IsEven(7) ? (byte)1 : (byte)0;
        Hardware.SCX = even;
        Hardware.SCY = odd;
        return even * 10 + odd;
    }

    public static int Deep()
    {
        Prepare();
        byte value = Sum(500);
        Hardware.SCX = value;
        return value;
    }

    public static int Signed()
    {
        Prepare();
        return SignedSum(500);
    }

    public static int Word()
    {
        Prepare();
        return Sum16(500);
    }

    public static int Integer()
    {
        Prepare();
        return Sum32(500);
    }

    public static int WideLow()
    {
        Prepare();
        return (int)Sum64(500);
    }

    public static int WideHigh()
    {
        Prepare();
        return (int)(Sum64(500) >> 32);
    }
}
