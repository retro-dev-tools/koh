public static class Program
{
    static T Identity<T>(T value) => value;

    static T Wrap<T>(T value) => Identity<T>(value);

    static T Recursive<T>(T value, int count) =>
        count == 0 ? value : Recursive<T>(value, count - 1);

    static T Local<T>(T value)
    {
        T copy = value;
        return Wrap(copy);
    }

    static T Make<T>(T value)
    {
        T[] items = new T[2];
        items[1] = value;
        return items[1];
    }

    static void Swap<T>(ref T left, ref T right)
    {
        T copy = left;
        left = right;
        right = copy;
    }

    static T Zero<T>() => default;

    static float Unused<T>(T value) => 0.25f;

    static int Expand<T>() => Expand<T[]>();

    static T Constrained<T>(T value)
        where T : struct => value;

    public static int Expanding() => Expand<int>();

    public static int Constraint() => Constrained<int>(42);

    public static int Referenced() =>
        Library.Forward<byte>(5) + (Library.Forward<ushort>(1000) >> 3);

    public static int PrimitiveWidths()
    {
        object marker = Wrap<object>(new Marker());
        return
            Wrap<sbyte>(-128) == -128
            && Wrap<short>(-32768) == -32768
            && Wrap<int>(int.MinValue) == int.MinValue
            && Wrap<uint>(0xfedcba98) == 0xfedcba98
            && Wrap<char>('\ud800') == '\ud800'
            && Make<bool>(true)
            && marker != null
            && Wrap<nint>((nint)(-123)) == -123
            && Wrap<nuint>((nuint)65535) == 65535
            ? 42
            : 0;
    }

    public static int Storage()
    {
        ushort first = 1000;
        ushort second = 42;
        Swap(ref first, ref second);
        string left = "Koh";
        string right = "Rust";
        Swap(ref left, ref right);
        return
            first == 42
            && second == 1000
            && left.Length == 4
            && right.Length == 3
            && Make<long>(-5000000001) == -5000000001
            && Make<byte>(231) == 231
            && Zero<ulong>() == 0
            ? 42
            : 0;
    }

    public static int Main()
    {
        byte small = Wrap<byte>(5);
        ushort wide = Wrap<ushort>(1000);
        Koh.GameBoy.Hardware.BGP = (byte)(small + (wide >> 3));
        return Koh.GameBoy.Hardware.BGP;
    }

    public static int Wide()
    {
        long signed = Recursive<long>(-5000000001, 3);
        ulong unsigned = Local<ulong>(0xfedcba9876543210);
        return signed == -5000000001 && unsigned == 0xfedcba9876543210 ? 42 : 0;
    }

    public static int References()
    {
        string text = Local<string>("Koh");
        int[] numbers = Wrap<int[]>(new int[] { 42 });
        return text.Length == 3 ? numbers[0] : 0;
    }
}

public sealed class Marker { }
