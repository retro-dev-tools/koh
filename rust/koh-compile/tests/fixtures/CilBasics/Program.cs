using Koh.GameBoy;

public static class Program
{
    static byte[] Results;
    static readonly byte[] Literal = { 9, 8, 7, 6, 5, 4, 3 };
    static byte[] Buffer = new byte[11];

    public static int Main() => RegisterLoop();

    static void Prepare(int count)
    {
        Hardware.SCY = 0;
        Hardware.SCX = 0;
        Hardware.BGP = 0;
        Hardware.WY = 0;
        Results = new byte[count * 4];
    }

    static void Store(int index, int value)
    {
        index *= 4;
        Results[index] = (byte)value;
        Results[index + 1] = (byte)(value >> 8);
        Results[index + 2] = (byte)(value >> 16);
        Results[index + 3] = (byte)(value >> 24);
    }

    static unsafe int Address()
    {
        fixed (byte* pointer = Results)
            return (ushort)pointer;
    }

    public static int RegisterLoop()
    {
        Prepare(1);
        byte value = 0;
        for (int i = 0; i < 5; i++)
            value = (byte)(value + 1);
        Hardware.BGP = value;
        Store(0, value);
        return Address();
    }

    public static int RegisterReadWrite()
    {
        Prepare(2);
        Hardware.SCY = 7;
        byte value = Hardware.SCY;
        Hardware.BGP = (byte)(value + 1);
        Store(0, value);
        Store(1, Hardware.BGP);
        return Address();
    }

    static byte AddOne(byte value) => (byte)(value + 1);

    public static int StaticCall()
    {
        Prepare(1);
        byte value = 0;
        for (int i = 0; i < 3; i++)
            value = AddOne(value);
        Hardware.BGP = value;
        Store(0, value);
        return Address();
    }

    static int Ternary()
    {
        int value = Hardware.SCY;
        Hardware.BGP = (byte)(value < 5 ? 9 : 1);
        return Hardware.BGP;
    }

    public static int TernaryLow()
    {
        Prepare(1);
        Hardware.SCY = 2;
        Store(0, Ternary());
        return Address();
    }

    public static int TernaryHigh()
    {
        Prepare(1);
        Hardware.SCY = 6;
        Store(0, Ternary());
        return Address();
    }

    public static int Arithmetic()
    {
        Prepare(4);
        int a = 13,
            b = 5;
        int result = a - b;
        result *= 3;
        result &= 0x1f;
        result |= 0x40;
        result ^= 8;
        result <<= 1;
        result >>= 2;
        int negative = -a;
        int complement = ~a;
        byte flags = 0;
        if (result == 40)
            flags += 1;
        if (negative < 0)
            flags += 2;
        if (complement > -20)
            flags += 4;
        if (a != b)
            flags += 8;
        if (b <= a)
            flags += 16;
        if (a >= b)
            flags += 32;
        Hardware.BGP = flags;
        Store(0, result);
        Store(1, negative);
        Store(2, complement);
        Store(3, flags);
        return Address();
    }

    static bool High(uint value) => value > 0x7fffffff;

    public static int Loops()
    {
        Prepare(6);
        uint u = 0;
        while (u < 5u)
            u++;
        int i = 0;
        while (i != 4)
            i++;
        Hardware.BGP = (byte)(u + (uint)i);
        Store(0, (int)u);
        Store(1, i);
        Store(2, (int)(u + (uint)i));
        Store(3, High(0x80000000) ? 1 : 0);
        Store(4, High(uint.MaxValue) ? 1 : 0);
        Store(5, High(0x7fffffff) ? 1 : 0);
        return Address();
    }

    static sbyte I1(int value) => (sbyte)value;

    static short I2(int value) => (short)value;

    static ushort U2(int value) => (ushort)value;

    public static int Conversions()
    {
        Prepare(7);
        int first = 300,
            second = 70000;
        sbyte a = (sbyte)first;
        short b = (short)second;
        ushort c = (ushort)second;
        int result = a + b + c;
        Hardware.BGP = (byte)(result & 255);
        Store(0, a);
        Store(1, b);
        Store(2, c);
        Store(3, result);
        Store(4, I1(128));
        Store(5, I2(65535));
        Store(6, U2(65535));
        return Address();
    }

    public static int ArrayLoop()
    {
        Prepare(2);
        int[] array = new int[5];
        array[0] = 10;
        array[1] = 20;
        array[2] = 30;
        array[3] = 40;
        array[4] = 50;
        int sum = 0;
        for (int i = 0; i < array.Length; i++)
            sum += array[i];
        Hardware.BGP = (byte)sum;
        Store(0, array.Length);
        Store(1, sum);
        return Address();
    }

    static int Len(byte[] array) => array.Length;

    static int LenInt(int[] array) => array.Length;

    public static int ArrayParameters()
    {
        Prepare(4);
        int[] array = new int[3];
        Hardware.BGP = (byte)LenInt(array);
        Hardware.SCY = (byte)Len(Literal);
        Hardware.WY = (byte)Len(Buffer);
        Hardware.SCX = 0xee;
        Store(0, LenInt(array));
        Store(1, Len(Literal));
        Store(2, Len(Buffer));
        Literal[0] = 42;
        Store(3, Literal[0]);
        return Address();
    }

    public static void Unsupported() => System.Console.WriteLine("hi");
}
