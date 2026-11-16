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
}
