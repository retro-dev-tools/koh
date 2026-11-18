using Koh.GameBoy;

public static unsafe class Program
{
    public static int Zero() => Run(255, 0, false, true, false);

    public static int ZeroOn() => Run(144, 0, true, false, false);

    public static int First() => Run(0, 1, true, true, false);

    public static int Last() => Run(143, 1, true, true, false);

    public static int Full() => Run(0, 144, true, true, false);

    public static int Cube() => Run(39, 64, true, false, false);

    public static int Double() => Run(0, 144, true, true, true);

    public static int BadFirst() => Run(144, 1, true, false, false);

    public static int BadWrap() => Run(255, 1, true, false, false);

    public static int BadEnd() => Run(143, 2, true, false, false);

    public static int Off() => Run(0, 1, false, false, false);

    private static int Run(byte first, byte count, bool on, bool interrupts, bool fast)
    {
        Hardware.DisableInterrupts();
        Hardware.IE = 0;
        Hardware.IF = 0;
        if (fast)
            Cgb.TryEnableDoubleSpeed();
        byte* source = (byte*)0xD100;
        *(byte*)0xFF70 = 2;
        for (int i = 0; i < 144; i++)
            source[i] = (byte)(i + 1);
        if (on)
            Lcd.On();
        else
            Lcd.Off();
        if (interrupts)
            Hardware.EnableInterrupts();
        Hardware.BGP = 0x42;
        Ppu.StreamScrollX(count == 0 ? (byte*)0xD100 : source, first, count);
        Hardware.BGP = 0x43;
        return 42;
    }

    [KohIntrinsic("hblankstream", 0xFF40)]
    private static void BadRegister(byte* source, byte first, byte count) { }

    [KohIntrinsic("hblankstream", 0xFF43)]
    private static int BadReturn(byte* source, byte first, byte count) => 0;

    [KohIntrinsic("hblankstream", 0xFF43)]
    private static void BadPointer(ushort source, byte first, byte count) { }

    [KohIntrinsic("hblankstream", 0xFF43)]
    private static void BadCount(byte* source, byte first, ushort count) { }

    public static void InvalidRegister() => BadRegister((byte*)0xD100, 0, 1);

    public static int InvalidReturn() => BadReturn((byte*)0xD100, 0, 1);

    public static void InvalidPointer() => BadPointer(0xD100, 0, 1);

    public static void InvalidCount() => BadCount((byte*)0xD100, 0, 1);
}
