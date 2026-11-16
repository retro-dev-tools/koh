public static class Program
{
    private static readonly byte[,] Map =
    {
        { 10, 11, 12, 13 },
        { 20, 21, 22, 23 },
        { 30, 31, 32, 33 },
    };

    private static byte Read(byte[,] array, int row, int column) => array[row, column];

    private static byte[,] Identity(byte[,] array) => array;

    public static int Main()
    {
        if (Map[0, 0] != 10 || Map[1, 2] != 22)
            return -1;
        byte[,] scratch = new byte[3, 5];
        scratch[0, 0] = 1;
        scratch[2, 4] = 99;
        scratch[1, 3] = 55;
        if (Read(scratch, 1, 3) != 55)
            return -1;
        byte[,] alias = Identity(scratch);
        alias[1, 3] = 56;
        Map[0, 0] = 14;
        return
            Read(Map, 0, 0) == 14
            && Read(Map, 2, 3) == 33
            && Read(Map, 2, 1) == 31
            && Read(Map, 0, 3) == 13
            && scratch[0, 0] == 1
            && scratch[2, 4] == 99
            && Read(scratch, 1, 3) == 56
            && scratch[0, 1] == 0
            && scratch.Length == 15
            && scratch.GetLength(0) == 3
            && scratch.GetLength(1) == 5
            && NullShapes.Main() == 42
            ? 42
            : -1;
    }
}

public static class NullShapes
{
    private static byte[,] Identity(byte[,] value) => value;

    private static ref byte[,] Cell(ref byte[,] value) => ref value;

    public static int Main()
    {
        byte[,] value = null;
        return Identity(value) == null && Cell(ref value) == null ? 42 : -1;
    }
}

public static class EmptyShapes
{
    public static int Main()
    {
        byte[,] rows = new byte[0, 65535];
        byte[,] columns = new byte[65535, 0];
        return
            rows != null
            && columns != null
            && rows != columns
            && rows.Length == 0
            && columns.Length == 0
            && rows.Rank == 2
            && rows.GetLength(0) == 0
            && rows.GetLength(1) == 65535
            && columns.GetLength(0) == 65535
            && columns.GetLength(1) == 0
            && rows.LongLength == 0
            && columns.GetLongLength(0) == 65535
            && rows.GetLowerBound(0) == 0
            && rows.GetUpperBound(0) == -1
            && rows.GetUpperBound(1) == 65534
            ? 42
            : -1;
    }
}

public static class BorrowedElements
{
    private static ref int Cell(int[,] values, int row, int column) => ref values[row, column];

    public static int Main()
    {
        int[,] values = new int[2, 3];
        values[0, 2] = -1234567;
        ref int cell = ref Cell(values, 1, 0);
        cell = 0x12345678;
        return values[0, 2] == -1234567 && values[1, 0] == 0x12345678 && values[1, 1] == 0
            ? 42
            : -1;
    }
}

public static class WideElements
{
    public static int Main()
    {
        short[,] signed = new short[2, 3];
        ushort[,] unsigned = new ushort[2, 3];
        long[,] wide = new long[2, 3];
        signed[1, 2] = -1234;
        unsigned[1, 2] = 0xFEDC;
        wide[1, 2] = -0x123456789ABCDEFL;
        return
            signed[1, 2] == -1234
            && unsigned[1, 2] == 0xFEDC
            && wide[1, 2] == -0x123456789ABCDEFL
            && signed[0, 2] == 0
            && unsigned[1, 0] == 0
            && wide[1, 1] == 0
            ? 42
            : -1;
    }
}

public static class InvalidShapes
{
    private static int Index(int value) => value;

    public static int NegativeRow() => (new byte[2, 3])[Index(-1), 0];

    public static int NegativeColumn() => (new byte[2, 3])[0, Index(-1)];

    public static int RowOverflow() => (new byte[2, 3])[2, 0];

    public static int ColumnOverflow() => (new byte[2, 3])[0, 3];

    public static int TruncatedIndex() => (new byte[2, 3])[0, Index(65536)];

    public static int InvalidDimension() => new byte[2, 3].GetLength(2);

    public static int NegativeDimension() => new byte[2, 3].GetLength(Index(-1));

    public static int NegativeSize() => new byte[Index(-1), 0].Length;

    public static int TruncatedSize() => new byte[0, Index(65536)].Length;

    public static int Capacity() => new byte[65, 65].Length;

    public static int ProductOverflow() => new byte[65535, 65535].Length;

    public static int NullIndex()
    {
        byte[,] value = null;
        return value[Index(-1), 65536];
    }

    public static int NullDimension()
    {
        byte[,] value = null;
        return value.GetLength(Index(-1));
    }
}

public sealed class RectangleItem
{
    public int Value;
}

public struct RectangleValue
{
    public long Wide;
    public RectangleItem Item;
    public byte[,] Map;
}

public static class AggregateShapes
{
    private static ref RectangleValue Cell(RectangleValue[,] values) => ref values[1, 2];

    private static ref RectangleItem ItemCell(RectangleItem[,] values) => ref values[0, 1];

    public static int Main()
    {
        RectangleItem item = new RectangleItem { Value = 7 };
        RectangleItem[,] items = new RectangleItem[2, 3];
        ItemCell(items) = item;
        RectangleValue[,] values = new RectangleValue[2, 3];
        values[1, 2] = new RectangleValue
        {
            Wide = -0x123456789ABCDEFL,
            Item = items[0, 1],
            Map = new byte[2, 3],
        };
        RectangleValue copy = values[1, 2];
        copy.Wide = 42;
        copy.Item.Value = 9;
        copy.Map[1, 2] = 11;
        ref RectangleValue cell = ref Cell(values);
        cell.Wide += 1;
        return
            values[1, 2].Wide == -0x123456789ABCDEEL
            && copy.Wide == 42
            && item.Value == 9
            && values[1, 2].Map[1, 2] == 11
            && values[0, 0].Item == null
            && values[0, 0].Map == null
            && values[0, 0].Wide == 0
            && items[1, 2] == null
            ? 42
            : -1;
    }
}
