public interface IRead
{
    int Read();
}

public struct Cell<T> : IRead
{
    public T Data;
    public int Value;

    public int Read() => ++Value;
}

public static class Program
{
    private static int Via<T>(ref T value)
        where T : struct, IRead => value.Read();

    public static int Byte()
    {
        var value = new Cell<byte> { Data = 7, Value = 40 };
        return Via(ref value) == 41 && value.Value == 41 && value.Data == 7 ? 42 : 0;
    }

    public static int Wide()
    {
        var value = new Cell<ulong> { Data = 0xfedcba9876543210UL, Value = 100 };
        return Via(ref value) == 101 && value.Value == 101 && value.Data == 0xfedcba9876543210UL
            ? 42
            : 0;
    }

    public static int DirectByte()
    {
        var value = new Cell<byte>();
        return value.Read();
    }

    public static int DirectWide()
    {
        var value = new Cell<ulong>();
        return value.Read();
    }
}
