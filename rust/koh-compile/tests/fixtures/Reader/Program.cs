public static class Program
{
    public static unsafe void Main()
    {
        int value = Add(17, 25);
        *(byte*)0xC900 = (byte)value;
    }

    public static int Add(int left, int right) => left + right;

    public static T Echo<T>(T value) => value;

    public static byte[] Literal() => new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A };

    public static byte[] OtherLiteral() => new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1 };

    public static unsafe void Update(ref int value, out long result, byte* pointer)
    {
        result = value;
        value = *pointer;
    }

    public static int[,] Matrix = new int[2, 3];

    public static unsafe delegate* unmanaged[Cdecl]<int, int> Callback;

    public static int WithException(int value)
    {
        try
        {
            return 100 / value;
        }
        catch (System.DivideByZeroException)
        {
            return 0;
        }
    }
}

public struct Pair<T>
{
    public T Value;

    public readonly T Read() => Value;
}

public ref struct RefCell
{
    public ref int Value;

    public RefCell(ref int value)
    {
        Value = ref value;
    }
}
