public static unsafe class Program
{
    private static byte counter;

    public static int Local()
    {
        byte cell = 42;
        byte* pointer = &cell;
        *pointer = (byte)(*pointer + 1);
        return cell;
    }

    public static int Indexed()
    {
        byte* array = stackalloc byte[4];
        array[0] = 10;
        array[2] = 20;
        return array[0] + array[2];
    }

    private static byte Add3(byte a, byte b, byte c) => (byte)(a + b + c);

    public static int Arguments() => Add3(10, 20, 12);

    private static ushort Add16(ushort a, ushort b) => (ushort)(a + b);

    public static int WordReturn() => Add16(300, 250);

    private static byte Next(byte value) => (byte)(value + 1);

    private static byte Twice(byte value) => Next(Next(value));

    public static int Nested() => Twice(40);

    private static int Recurse(int n, int seed) => n == 0 ? seed : Recurse(n - 1, seed) + n;

    public static int Recursive() => Recurse(3, 36);

    private static int Even(int n, int seed) => n == 0 ? seed : Odd(n - 1, seed) + n;

    private static int Odd(int n, int seed) => n == 0 ? seed : Even(n - 1, seed) + n;

    public static int Mutual() => Even(3, 36);

    private static int Classify(int value) =>
        value switch
        {
            1 => 11,
            2 => 22,
            _ => 99,
        };

    public static int CaseOne() => Classify(1);

    public static int CaseTwo() => Classify(2);

    public static int CaseDefault() => Classify(7);

    public static int Boot() => 42;

    public static int Mmio()
    {
        *(byte*)0xff42 = 0x37;
        return *(byte*)0xff42;
    }

    public static int ByteTable()
    {
        byte[] values = { 5, 10, 15, 20 };
        return values[2];
    }

    public static int WordTable()
    {
        ushort[] values = { 1000, 2000, 3000 };
        return values[0];
    }

    public static int RomByte() => ((byte*)0x3ff0)[2];

    public static int RomWord() => *(ushort*)0x3ff4;

    private static void Increment() => counter++;

    public static int Shared()
    {
        counter = 0;
        Increment();
        Increment();
        return counter;
    }

    private static int ByteIndex(int index)
    {
        byte* array = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
            array[i] = (byte)((i + 1) * 10);
        return array[index];
    }

    public static int DynamicByte() => ByteIndex(2);

    private static int WordIndex(int index)
    {
        ushort* array = stackalloc ushort[3];
        array[0] = 100;
        array[1] = 200;
        array[2] = 300;
        return array[index];
    }

    public static int DynamicWord() => WordIndex(1);

    private static int Dereference(byte* pointer) => *pointer;

    public static int PointerArgument()
    {
        byte cell = 77;
        return Dereference(&cell);
    }

    private static int Store(int index)
    {
        byte* array = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
            array[i] = (byte)((i + 1) * 10);
        array[index] = 99;
        return array[0] == 10 && array[1] == 20 && array[2] == 30 && array[3] == 99
            ? array[index]
            : 0;
    }

    public static int DynamicStore() => Store(3);
}
