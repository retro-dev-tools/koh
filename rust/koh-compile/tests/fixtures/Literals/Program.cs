using System;

public static class Program
{
    private static byte ReadLocal(int index)
    {
        byte[] values = { 11, 22, 33, 44, 55 };
        return values[index];
    }

    private static byte ReadUtf8(int index)
    {
        ReadOnlySpan<byte> value = "Koh!"u8;
        return value[index];
    }

    public static int ArrayValues() => (ReadLocal(1) << 8) | ReadLocal(4);

    public static int Utf8Values() => (ReadUtf8(0) << 8) | ReadUtf8(3);

    public static int Utf8Length() => "Koh!"u8.Length;

    private static byte ChangeLocal(bool change)
    {
        byte[] values = { 11, 22, 33, 44, 55 };
        if (change)
            values[1] = 99;
        return values[1];
    }

    public static int ArrayMutations() => (ChangeLocal(true) << 8) | ChangeLocal(false);

    public static int Utf8Unicode()
    {
        ReadOnlySpan<byte> value = "é🐉"u8;
        return (value.Length << 8) | value[5];
    }

    public static int Utf8Prefixes()
    {
        ReadOnlySpan<byte> a = "Koh"u8;
        ReadOnlySpan<byte> b = "Koh\0"u8;
        return (a.Length << 8) | b.Length | b[3];
    }

    private static ReadOnlySpan<byte> Literal() => "Koh!"u8;

    private static int FromView(ReadOnlySpan<byte> value) => value[0];

    public static int Utf8Calls() => FromView(Literal());

    public static int Utf8PastEnd() => ReadUtf8(4);

    public static int Utf8Negative() => ReadUtf8(-1);

    public static int Utf8Empty() => ""u8.Length;

    public static int Utf8Default()
    {
        ReadOnlySpan<byte> value = default;
        return value.Length;
    }

    public static int Utf8EmptyBounds()
    {
        ReadOnlySpan<byte> value = ""u8;
        return value[0];
    }

    public static int Utf8Slice() => "Koh!"u8.Slice(1).Length;

    public static int WideSpan()
    {
        ReadOnlySpan<ushort> value = default;
        return value.Length;
    }

    public static int MutableSpan()
    {
        Span<byte> value = default;
        return value.Length;
    }
}
