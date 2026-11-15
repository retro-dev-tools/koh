using Koh.GameBoy;

public static class EmptyAligned
{
    [KohAligned(4096)]
    static byte[] Value = new byte[0];

    public static int Main() => Value.Length;
}

public static class OverflowAligned
{
    [KohAligned(4096)]
    static byte[] Value = new byte[1];

    public static int Main() => Value.Length;
}

public static unsafe class AliasAligned
{
    [KohAligned(16)]
    static byte[] First;

    [KohAligned(256)]
    static byte[] Second;

    static AliasAligned()
    {
        byte[] value;
        if (Cgb.IsColor())
            value = new byte[8];
        else
            value = new byte[8];
        First = value;
        Second = value;
    }

    public static int Main()
    {
        First[0] = 42;
        fixed (byte* p = &Second[0])
            return First == Second && ((ushort)p & 255) == 0 ? p[0] : -1;
    }
}

public static class FactoryAligned
{
    [KohAligned(256)]
    static byte[] Value = Make();

    static byte[] Make() => new byte[8];

    public static int Main() => Value.Length;
}

public static unsafe class ScalarAligned
{
    static byte Pad;

    [KohAligned(16)]
    static ushort Value;

    public static int Main()
    {
        Pad = 1;
        Value = 41;
        fixed (ushort* p = &Value)
            return ((ushort)p & 15) == 0 ? Pad + *p : -1;
    }
}
