using Koh.GameBoy;

public static unsafe class Program
{
    public static int Phase0() => Copy(0);

    public static int Phase1() => Copy(1);

    public static int Phase2() => Copy(2);

    public static int Phase3() => Copy(3);

    public static int Phase4() => Copy(4);

    public static int Phase5() => Copy(5);

    public static int Phase6() => Copy(6);

    public static int Phase7() => Copy(7);

    public static int Zero() => Copy(0, 0);

    public static int On16() => Copy(0, 16);

    public static int On1024() => Copy(0, 1024);

    public static int On1040() => Copy(0, 1040);

    public static int On1920() => Copy(0, 1920);

    public static int Off2048() => Copy(0, 2048, false);

    private static int Copy(byte delay, ushort count = 2048, bool lcdOn = true)
    {
        byte* source = Mem.Alloc(2048);
        Mem.Fill(source, 0x3C, 1024);
        Mem.Fill(source + 1024, 0xC3, 1024);
        if (lcdOn)
            Lcd.On();
        for (byte i = 0; i < delay; i++)
            Hardware.Nop();
        Cgb.CopyToVram(source, 0x8000, count);
        return 0x43;
    }
}
