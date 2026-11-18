using Koh.GameBoy;

public static class Program
{
    public static int Compare()
    {
        byte a = 0;
        for (int i = 0; i < 200; i++)
            a++;
        byte b = 0;
        for (int i = 0; i < 50; i++)
            b++;
        byte result = 0;
        if (a < b)
            result = 1;
        Hardware.BGP = result;
        return 42;
    }

    public static int Accumulate()
    {
        byte sum = 0;
        byte step = 3;
        for (byte i = 0; i < 50; i++)
            sum = (byte)(sum + step);
        Hardware.BGP = sum;
        return 42;
    }

    public static int WideAccumulate()
    {
        int sum = 0;
        byte step = 3;
        for (byte i = 0; i < 50; i++)
            sum += step;
        Hardware.BGP = (byte)sum;
        return 42;
    }

    public static int WrappingAccumulate()
    {
        byte sum = 0;
        for (byte i = 0; i < 200; i++)
            sum = (byte)(sum + 3);
        Hardware.BGP = sum;
        return 42;
    }
}
