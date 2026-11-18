using Koh.GameBoy;

public static class Program
{
    public static int Main()
    {
        Hardware.DisableInterrupts();
        Hardware.IE = 0x15;
        Hardware.IF = 0x15;
        bool first = Cgb.TryEnableDoubleSpeed();
        bool second = Cgb.TryEnableDoubleSpeed();
        Hardware.SCX = 1;
        return
            first == Cgb.IsColor()
            && second == first
            && Hardware.IE == 0x15
            && (Hardware.IF & 0x15) == 0x15
            ? 42
            : 1;
    }

    public static int Raw()
    {
        Hardware.IE = 0;
        Hardware.DisableInterrupts();
        Hardware.EnableInterrupts();
        Hardware.Nop();
        Hardware.DisableInterrupts();
        Hardware.SCX = 1;
        return 42;
    }

    [KohIntrinsic("di")]
    private static int InvalidReturn() => 0;

    [KohIntrinsic("nop")]
    private static void InvalidParameter(int value) { }

    [KohIntrinsic("stop", 0)]
    private static void InvalidAddress() { }

    public static int BadReturn() => InvalidReturn();

    public static void BadParameter() => InvalidParameter(1);

    public static void BadAddress() => InvalidAddress();

    public static int Wait()
    {
        Hardware.DisableInterrupts();
        Hardware.LCDC = 0;
        Hardware.IE = 1;
        Hardware.IF = 0;
        Hardware.Halt();
        Hardware.SCX = 1;
        return 42;
    }
}
