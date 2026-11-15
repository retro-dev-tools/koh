using Koh.GameBoy;
using Koh.Samples.Gb2048CSharp;

enum EI8 : sbyte
{
    MinusOne = -1,
    MinusTwo = -2,
    Zero = 0,
}

enum EU8 : byte
{
    Value = 255,
}

enum EI16 : short
{
    Value = -1234,
}

enum EU16 : ushort
{
    Value = 0xFEDC,
}

enum EI32 : int
{
    Value = int.MinValue,
}

enum EU32 : uint
{
    Value = uint.MaxValue,
}

enum EI64 : long
{
    Value = long.MinValue,
}

enum EU64 : ulong
{
    Value = 0xFEDCBA9876543210,
}

public static unsafe class Program
{
    [KohIntrinsic("register", 0xFF70)]
    static byte Bank
    {
        get => 0;
        set { }
    }

    public static int Left() => Run(Direction.Left);

    public static int Right() => Run(Direction.Right);

    public static int Up() => Run(Direction.Up);

    public static int Down() => Run(Direction.Down);

    public static int BankedLeft() => Banked(Direction.Left);

    public static int BankedRight() => Banked(Direction.Right);

    public static int BankedUp() => Banked(Direction.Up);

    public static int BankedDown() => Banked(Direction.Down);

    static int Banked(Direction direction)
    {
        Bank = 2;
        int value = Run(direction);
        return (Bank & 7) == 2 ? value : -1;
    }

    static int Run(Direction direction)
    {
        Board.Reset();
        byte[] input = { 1, 1, 1, 1, 1, 1, 2, 0, 0, 2, 0, 2, 3, 0, 3, 3 };
        for (byte i = 0; i < 16; i++)
            Board.SetCell(i, input[i]);
        bool changed = Board.Slide(direction);
        return Board.Score
            | (changed ? 1 << 16 : 0)
            | (Board.HasWon() ? 1 << 17 : 0)
            | (Board.CanMove() ? 1 << 18 : 0);
    }

    public static int Scratch() => Recurse(8);

    static int Recurse(int depth)
    {
        byte* values = stackalloc byte[8];
        values[0] = (byte)(depth + 20);
        int child = depth == 0 ? 0 : Recurse(depth - 1);
        return child + values[0];
    }

    public static int Zeroed()
    {
        byte* values = stackalloc byte[65];
        for (int i = 0; i < 65; i++)
            if (values[i] != 0)
                return -1;
        values[0] = 0xAB;
        values[64] = 0xCD;
        return values[0] + values[64];
    }

    public static int Empty()
    {
        byte* value = stackalloc byte[0];
        return value == null ? 42 : -1;
    }

    static EI8 Small;

    static EI8 Echo(EI8 value) => value;

    static EU8 Echo(EU8 value) => value;

    static EI16 Echo(EI16 value) => value;

    static EU16 Echo(EU16 value) => value;

    static EI32 Echo(EI32 value) => value;

    static EU32 Echo(EU32 value) => value;

    static EI64 Echo(EI64 value) => value;

    static EU64 Echo(EU64 value) => value;

    static void Decrement(ref EI8 value) => value = (EI8)((sbyte)value - 1);

    public static int BankedEnums()
    {
        Bank = 2;
        int value = Enums();
        return (Bank & 7) == 2 ? value : -1;
    }

    public static int Enums()
    {
        EI8 value = EI8.MinusOne;
        Decrement(ref value);
        Small = value;
        EI8[] narrow = { EI8.MinusOne, EI8.Zero, EI8.MinusTwo };
        narrow[1] = value;
        EU64[] wide = new EU64[1];
        wide[0] = Echo(EU64.Value);
        if (Small != EI8.MinusTwo || narrow[1] != EI8.MinusTwo || wide[0] != EU64.Value)
            return -1;
        return
            Echo(EI8.MinusOne) == EI8.MinusOne
            && Echo(EU8.Value) == EU8.Value
            && Echo(EI16.Value) == EI16.Value
            && Echo(EU16.Value) == EU16.Value
            && Echo(EI32.Value) == EI32.Value
            && Echo(EU32.Value) == EU32.Value
            && Echo(EI64.Value) == EI64.Value
            ? 42
            : -2;
    }

    static void ModifyPointer(ref int* pointer) => *pointer = 0x12345678;

    public static int PointerReference()
    {
        int value = 17;
        int* pointer = &value;
        ModifyPointer(ref pointer);
        return value;
    }

    public static int ExhaustedStack() => LargeRecursion(64);

    static int LargeRecursion(int depth)
    {
        byte* value = stackalloc byte[256];
        value[0] = (byte)depth;
        return depth == 0 ? value[0] : value[0] + LargeRecursion(depth - 1);
    }

    public static int Oversized()
    {
        byte* value = stackalloc byte[4096];
        return value[0];
    }

    public static int Dynamic() => Allocate(4);

    static int Allocate(int count)
    {
        byte* value = stackalloc byte[count];
        return value[0];
    }
}
