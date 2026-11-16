using Koh.GameBoy;

public static unsafe class Program
{
    [KohIntrinsic("vramwrite")]
    private static void Write(byte* destination, byte value) => *destination = value;

    [KohIntrinsic("vramwrite")]
    private static byte WrongReturn(byte* destination, byte value) => value;

    [KohIntrinsic("vramwrite", 0x8000)]
    private static void WrongAddress(byte* destination, byte value) { }

    [KohIntrinsic("vramwrite")]
    private static void WrongValue(byte* destination, int value) { }

    public static int RejectReturn() => WrongReturn((byte*)0x8001, 1);

    public static void RejectAddress() => WrongAddress((byte*)0x8001, 1);

    public static void RejectValue() => WrongValue((byte*)0x8001, 1);

    public static int Main()
    {
        Write((byte*)0x8001, 0x6d);
        return 42;
    }

    public static int End()
    {
        Write((byte*)0x9fff, 0xfe);
        return 42;
    }

    public static int BankOne()
    {
        Cgb.SelectVramBank(1);
        Write((byte*)0x8001, 0x6d);
        return 42;
    }

    public static int Large()
    {
        byte* padding = stackalloc byte[200];
        byte first = Hardware.DIV;
        byte last = Hardware.LY;
        padding[0] = first;
        padding[199] = last;
        Write((byte*)0x8001, 0x36);
        return padding[0] == first && padding[199] == last ? 42 : -1;
    }
}
