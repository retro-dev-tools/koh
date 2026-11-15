using System.Runtime.InteropServices;
using Koh.GameBoy;

public static unsafe class Program
{
    static Triple[] Saved;

    [KohIntrinsic("register", 0xFF70)]
    static byte Bank
    {
        get => 0;
        set { }
    }

    static Triple Read(Triple[] values) => values[0];

    static void Write(Triple[] values, Triple value) => values[1] = value;

    static Large ReadLarge(Large[] values) => values[0];

    static void WriteLarge(Large[] values, Large value) => values[1] = value;

    static ref Triple Element(Triple[] values, int index) => ref values[index];

    static Triple[] Maybe(bool choose, Triple[] values) => choose ? values : null;

    static int Far() => 65536;

    static int BadSize() => -1;

    public static int Banked()
    {
        Bank = 2;
        int result = Main();
        return (Bank & 7) == 2 ? result : -99;
    }

    public static int Main()
    {
        Triple[] values = new Triple[3];
        for (int i = 0; i < values.Length; i++)
            if (values[i].X != 0 || values[i].Y != 0 || values[i].Z != 0)
                return -1;
        values[0] = new Triple
        {
            X = 1,
            Y = 2,
            Z = 3,
        };
        Triple copy = Read(values);
        Write(values, copy);
        Element(values, 1).Y = 9;
        Saved = values;
        ref Triple alias = ref Element(Saved, 2);
        alias.X = 7;
        values[0].X = 4;
        copy.X = 99;
        Triple[] empty = new Triple[0];
        Triple[] another = new Triple[0];
        Odd[] odd = new Odd[2];
        odd[1].Bits = 0x12345678;
        odd[1].Tail = 0x9A;
        Large[] large = new Large[2];
        large[0].Tag = 0xAB;
        large[0].Wide = 0x0123456789ABCDEF;
        Large big = ReadLarge(large);
        WriteLarge(large, big);
        large[0].Tag = 0xCD;
        return
            values[0].X == 4
            && values[0].Y == 2
            && values[0].Z == 3
            && values[1].X == 1
            && values[1].Y == 9
            && values[1].Z == 3
            && values[2].X == 7
            && values[2].Y == 0
            && values[2].Z == 0
            && empty != null
            && another != null
            && empty != another
            && empty.Length == 0
            && odd[0].Bits == 0
            && odd[0].Tail == 0
            && odd[1].Bits == 0x12345678
            && odd[1].Tail == 0x9A
            && large[0].Tag == 0xCD
            && large[0].Wide == 0x0123456789ABCDEF
            && large[1].Tag == 0xAB
            && large[1].Wide == big.Wide
            && big.Tag == 0xAB
            && Maybe(true, values) == values
            && Maybe(false, values) == null
            && copy.X == 99
            ? 42
            : -2;
    }

    public static int BankedAligned()
    {
        Bank = 2;
        int result = Aligned();
        return (Bank & 7) == 2 ? result : -99;
    }

    public static int Aligned()
    {
        AlignedState.Values[1].Z = 0xCD;
        return
            AlignedState.Values.Length == 2
            && AlignedState.Values[0].X == 0
            && AlignedState.Values[1].Z == 0xCD
            ? 42
            : -1;
    }

    public static int BankedPinned()
    {
        Bank = 2;
        int result = Pinned();
        return (Bank & 7) == 2 ? result : -99;
    }

    public static int Pinned()
    {
        Triple[] values = new Triple[1];
        values[0] = new Triple { X = 9, Z = 6 };
        fixed (Triple* pointer = values)
            pointer[0].Y = 7;
        return values[0].X == 9 && values[0].Y == 7 && values[0].Z == 6 ? 42 : -1;
    }

    public static int Capacity()
    {
        Triple[] values = new Triple[1364];
        values[1363].Z = 0xE1;
        Triple[] empty = new Triple[0];
        return values.Length == 1364 && values[1363].Z == 0xE1 && empty.Length == 0 ? 42 : -1;
    }

    public static int HugeEmpty() => new Huge[0].Length == 0 ? 42 : -1;

    public static int NestedValues()
    {
        Line line;
        line.Start.X = 1;
        line.Start.Y = 2;
        line.End.X = 3;
        line.End.Y = 4;
        Point[] points = new Point[3];
        points[0].X = 5;
        points[1].X = 6;
        points[2].X = 7;
        byte sum = (byte)(
            line.Start.X
            + line.Start.Y
            + line.End.X
            + line.End.Y
            + points[0].X
            + points[1].X
            + points[2].X
        );
        Hardware.BGP = sum;
        return Hardware.BGP == 28 && points[0].Y == 0 && points[1].Y == 0 && points[2].Y == 0
            ? 42
            : -1;
    }

    public static int Null()
    {
        Triple[] values = null;
        return values[0].X;
    }

    public static int Bounds() => (new Triple[1])[Far()].X;

    public static int Negative() => new Triple[BadSize()].Length;

    public static int OverOdd() => new Odd[819].Length;

    public static int Wrap() => new Large[1024].Length;

    public static int HugeTooBig() => new Huge[1].Length;
}

public struct Triple
{
    public byte X,
        Y,
        Z;
}

[StructLayout(LayoutKind.Explicit, Size = 5)]
public struct Odd
{
    [FieldOffset(0)]
    public uint Bits;

    [FieldOffset(4)]
    public byte Tail;
}

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct Large
{
    public byte Tag;
    public long Wide;
}

[StructLayout(LayoutKind.Sequential, Size = 4096)]
public struct Huge
{
    public byte Value;
}

static class AlignedState
{
    [KohAligned(16)]
    internal static Triple[] Values = new Triple[2];
}

public struct Point
{
    public byte X,
        Y;
}

public struct Line
{
    public Point Start,
        End;
}
