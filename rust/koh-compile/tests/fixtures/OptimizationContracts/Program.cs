public static unsafe class Program
{
    public static int Add(int x, int y) => unchecked(x + y);

    public static int ByteAdd(int x, int y) => unchecked((byte)(x + y));

    public static int SignedDivide(int x, int y) => (sbyte)x / (sbyte)y;

    public static int UnsignedDivide(int x, int y) => (byte)x / (byte)y;

    public static int SignedCompare(int x, int y) => (sbyte)x < (sbyte)y ? 1 : 0;

    public static int UnsignedCompare(int x, int y) => (byte)x < (byte)y ? 1 : 0;

    public static long Convert(int x, int y) =>
        (byte)x | ((long)(byte)y << 16) | ((long)(uint)(int)(sbyte)y << 32);

    public static long Identities(int x, int y)
    {
        byte b = (byte)x;
        return (byte)(b + 0)
            | ((long)(byte)(b * 0) << 8)
            | ((long)(byte)(b & 0xff) << 16)
            | ((long)(byte)(b ^ b) << 24)
            | ((long)(byte)(b << 0) << 32)
            | ((long)(byte)(b / 1) << 40);
    }

    public static int DeadChain(int x, int y)
    {
        int first = unchecked(x + 1);
        int unused = unchecked(first + 1);
        return x;
    }

    public static int WriteOnly(int x, int y)
    {
        int cell = 5;
        int* pointer = &cell;
        *pointer = y;
        return x;
    }

    public static int Cascade(int x, int y) => unchecked((x + y) * 10);

    public static int Duplicate(int x, int y) => unchecked((x + y) + (x + y));

    public static int MultiplyPower(int x, int y) => unchecked((ushort)(x * 8));

    public static int UnsignedPower(int x, int y) => (ushort)x / 4;

    public static int UnsignedRemainder(int x, int y) => (byte)x % 8;

    public static int MultiplyOther(int x, int y) => unchecked((byte)(x * 3));

    public static int SignedPower(int x, int y) => (sbyte)x / 4;

    public static int NegativePower(int x, int y) => (sbyte)x / -128;

    public static int Join(int x, int y)
    {
        int result;
        if ((byte)x > 10)
            result = 1;
        else
            result = 2;
        return result;
    }

    public static int FoldedJoin(int x, int y) => x != 0 ? 2 + 3 : 7;

    public static int ScalarLocal(int x, int y)
    {
        byte cell = unchecked((byte)x);
        return unchecked((byte)(cell + cell));
    }

    public static int RepeatedLoads(int x, int y)
    {
        int cell = x;
        int* pointer = &cell;
        return unchecked(*pointer + *pointer);
    }

    public static int Escaped(int x, int y)
    {
        int cell = x;
        int before = cell;
        Change(&cell, y);
        return unchecked(before + cell);
    }

    private static void Change(int* pointer, int value)
    {
        *pointer = value;
    }

    public static int Distinct(int x, int y)
    {
        int first = x,
            second = y;
        int* a = &first;
        int* b = &second;
        *a = unchecked(*a + 1);
        return unchecked(*a + *b);
    }

    public static int IndexedAlias(int x, int y)
    {
        byte* data = stackalloc byte[4];
        data[0] = 0x11;
        data[1] = 0x22;
        data[3] = 0x44;
        byte* first = data + 2;
        byte* second = data + 2;
        *first = (byte)x;
        *second = (byte)y;
        return data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
    }

    public static int UnusedBusRead(int x, int y)
    {
        int ignored = *(byte*)0xff44;
        return y;
    }

    public static int UnusedCheckedLoad(int x, int y)
    {
        int[] array = null!;
        int ignored = array[x];
        return y;
    }

    public static int InlineLeaf(int x, int y) => AddThree(x);

    private static int AddThree(int x) => unchecked(x + 3);

    public static int Dead(int x, int y) => throw new System.Exception();
}
