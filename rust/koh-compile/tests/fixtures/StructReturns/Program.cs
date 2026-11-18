public static class Program
{
    static byte[] Results;
    static Point Cursor;

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

    static Line Make(byte a, byte b, byte c, byte d)
    {
        Line line = default;
        line.Set(0, a);
        line.Set(1, b);
        line.Set(2, c);
        line.Set(3, d);
        return line;
    }

    static Line Reverse(Line line)
    {
        Line result = default;
        for (int i = 0; i < 4; i++)
            result.Set(i, line.Get(3 - i));
        line.Set(0, 77);
        return result;
    }

    public static int Lines()
    {
        Results = new byte[52];
        Line line = Make(10, 20, 30, 40);
        for (int i = 0; i < 4; i++)
            Store(i, line.Get(i));
        Store(4, line.Sum());
        Line reverse = Reverse(line);
        for (int i = 0; i < 4; i++)
            Store(5 + i, reverse.Get(i));
        Store(9, line.Get(0));
        line.Set(0, 99);
        Store(10, reverse.Get(3));
        Store(11, line.Get(0));
        Store(12, Make(1, 2, 3, 4).Sum());
        return Address();
    }

    public static int Factories()
    {
        Results = new byte[32];
        Cursor = Point.Make(100, -50);
        Store(0, Cursor.X);
        Store(1, Cursor.Y);
        Point moved = Cursor.Offset(10, 60);
        Store(2, moved.X);
        Store(3, moved.Y);
        Store(4, Cursor.X);
        Store(5, Cursor.Y);
        Point sum = Point.Make(0, 0);
        for (short i = 1; i <= 5; i++)
            sum = sum.Offset(i, (short)(i * 2));
        Store(6, sum.X);
        Store(7, sum.Y);
        return Address();
    }

    static Pair Fib(byte n)
    {
        Pair result = default;
        if (n == 0)
        {
            result.Lo = 0;
            result.Hi = 1;
            return result;
        }
        Pair previous = Fib((byte)(n - 1));
        result.Lo = previous.Hi;
        result.Hi = (ushort)(previous.Lo + previous.Hi);
        return result;
    }

    public static int Recursion()
    {
        Results = new byte[32];
        Pair zero = Fib(0);
        Pair one = Fib(1);
        Pair ten = Fib(10);
        Pair eleven = Fib(11);
        Store(0, zero.Lo);
        Store(1, zero.Hi);
        Store(2, one.Lo);
        Store(3, one.Hi);
        Store(4, ten.Lo);
        Store(5, ten.Hi);
        Store(6, eleven.Lo);
        Store(7, eleven.Hi);
        return Address();
    }

    static Box Tagged<T>(T ignored, int value, byte tag)
    {
        Box result = default;
        result.Value = value;
        result.Tag = tag;
        return result;
    }

    public static int Generic()
    {
        Results = new byte[16];
        Box first = Tagged<byte>(0, 200, 1);
        Box second = Tagged<ushort>(0, 40000, 2);
        Store(0, first.Value);
        Store(1, first.Tag);
        Store(2, second.Value);
        Store(3, second.Tag);
        return Address();
    }

    public static int Enumeration()
    {
        Results = new byte[24];
        int sum = 0;
        int index = 0;
        Range4 range = default;
        foreach (int value in range)
        {
            Store(index++, value);
            sum += value;
        }
        int count = 0;
        foreach (int value in range)
            count++;
        Store(4, sum);
        Store(5, count);
        return Address();
    }
}

struct Line
{
    byte A,
        B,
        C,
        D;

    public byte Get(int index) =>
        index == 0 ? A
        : index == 1 ? B
        : index == 2 ? C
        : D;

    public void Set(int index, byte value)
    {
        if (index == 0)
            A = value;
        else if (index == 1)
            B = value;
        else if (index == 2)
            C = value;
        else
            D = value;
    }

    public int Sum() => A + B + C + D;
}

struct Point
{
    public short X,
        Y;

    public static Point Make(short x, short y)
    {
        Point result = default;
        result.X = x;
        result.Y = y;
        return result;
    }

    public Point Offset(short dx, short dy) => Make((short)(X + dx), (short)(Y + dy));
}

struct Pair
{
    public ushort Lo,
        Hi;
}

struct Box
{
    public int Value;
    public byte Tag;
}

struct Range4
{
    public RangeEnumerator GetEnumerator()
    {
        RangeEnumerator result = default;
        result.Index = -1;
        return result;
    }
}

struct RangeEnumerator
{
    public int Index;

    public bool MoveNext()
    {
        Index++;
        return Index < 4;
    }

    public int Current => Index * 10;
}
