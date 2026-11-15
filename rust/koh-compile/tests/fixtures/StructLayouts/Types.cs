using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public struct Empty { }

public struct Plain
{
    public byte A;
    public short B;
    public int C;
    public long D;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Packed
{
    public byte A;
    public short B;
    public int C;
    public long D;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct PackedTwo
{
    public byte A;
    public short B;
    public int C;
    public long D;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct Overlay
{
    [FieldOffset(0)]
    public int I;

    [FieldOffset(0)]
    public short S;

    [FieldOffset(3)]
    public byte B;
}

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct Large
{
    public byte Tag;
}

public struct Nested
{
    public byte Tag;
    public Plain Body;
    public ushort Tail;
}

public enum Small : sbyte
{
    Negative = -1,
}

public struct WithEnum
{
    public Small A;
    public ushort B;
}

public struct WithReference
{
    public object Value;
}

public struct Generic<T>
{
    public T Value;
}

[StructLayout(LayoutKind.Auto)]
public struct Automatic
{
    public int Value;
}

[InlineArray(4)]
public struct Inline
{
    private int element;
}

[StructLayout(LayoutKind.Sequential, Size = 5)]
public struct OddSequential
{
    public int Value;
}

[StructLayout(LayoutKind.Explicit, Size = 5)]
public struct OddExplicit
{
    [FieldOffset(0)]
    public int Value;
}

public static unsafe class Program
{
    public static void Main()
    {
        System.Console.WriteLine(sizeof(OddSequential));
        System.Console.WriteLine(sizeof(OddExplicit));
    }
}
