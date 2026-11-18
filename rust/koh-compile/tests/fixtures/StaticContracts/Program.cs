using System;
using Koh.GameBoy;

public static unsafe class Program
{
    public static int Counter()
    {
        byte* p = (byte*)0xDF00;
        p[0] = CounterState.Value;
        for (int i = 0; i < 5; i++)
            CounterState.Bump();
        p[1] = CounterState.Value;
        Hardware.LCDC = CounterState.Value;
        CounterState.Value = 255;
        CounterState.Bump();
        p[2] = CounterState.Value;
        return 42;
    }

    public static int Table()
    {
        byte* p = (byte*)0xDF00;
        p[0] = TableState.Read(2);
        p[1] = TableState.Read(4);
        Hardware.LCDC = p[0];
        Hardware.BGP = p[1];
        TableState.Values[2] = 99;
        p[2] = TableState.Read(2);
        for (int i = 0; i < 5; i++)
            p[3 + i] = TableState.Values[i];
        return 42;
    }

    public static int Constant()
    {
        *(byte*)0xDF00 = ConstantState.Read();
        Hardware.LCDC = ConstantState.Read();
        return 42;
    }

    public static int MutableArray()
    {
        byte* p = (byte*)0xDF00;
        p[0] = MutableState.Cells[0];
        p[1] = MutableState.Cells[3];
        MutableState.Set(0, 7);
        MutableState.Set(3, 9);
        p[2] = MutableState.Cells[0];
        p[3] = MutableState.Cells[3];
        Hardware.LCDC = p[2];
        Hardware.BGP = p[3];
        byte[] old = MutableState.Cells;
        MutableState.Cells = new byte[4];
        p[4] = MutableState.Cells[0];
        p[5] = MutableState.Cells[3];
        p[6] = old[0];
        p[7] = old[3];
        return 42;
    }

    public static int CachedLambda()
    {
        int* p = (int*)0xDF00;
        p[0] = CacheState.Call(5);
        p[1] = CacheState.Call(5);
        p[2] = CacheState.Initializations;
        p[3] = CacheState.Calls;
        Hardware.BGP = (byte)p[0];
        return 42;
    }

    public static int LargeArray()
    {
        LargeState.Cells[0] = 7;
        LargeState.Cells[4999] = 9;
        return LargeState.Cells[0] + LargeState.Cells[4999];
    }
}

public static class CounterState
{
    public static byte Value;

    public static void Bump() => Value = (byte)(Value + 1);
}

public static class TableState
{
    public static readonly byte[] Values = { 10, 20, 30, 40, 50 };

    public static byte Read(int index) => Values[index];
}

public static class ConstantState
{
    public const byte ConstVal = 42;

    public static byte Read() => ConstVal;
}

public static class MutableState
{
    public static byte[] Cells = new byte[4];

    public static void Set(int index, byte value) => Cells[index] = value;
}

public static class CacheState
{
    public static int Initializations;
    public static int Calls;

    static CacheState()
    {
        Initializations++;
    }

    public static int Call(int value)
    {
        Func<int, int> function = x =>
        {
            Calls++;
            return x + 1;
        };
        return function(value);
    }
}

public static class LargeState
{
    public static byte[] Cells = new byte[5000];
}
