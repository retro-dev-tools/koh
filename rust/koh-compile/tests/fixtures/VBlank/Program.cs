using Koh.GameBoy;

public static class Program
{
    public static int Phase0() => Wait(0);

    public static int Phase1() => Wait(1);

    public static int Phase2() => Wait(2);

    public static int Phase3() => Wait(3);

    public static int Phase4() => Wait(4);

    public static int Phase5() => Wait(5);

    public static int Phase6() => Wait(6);

    public static int Phase7() => Wait(7);

    public static int Twice()
    {
        Lcd.On();
        Ppu.WaitVBlank();
        Ppu.WaitVBlank();
        return Hardware.LY;
    }

    private static int Wait(byte delay)
    {
        Lcd.On();
        for (byte i = 0; i < delay; i++)
            Hardware.Nop();
        Ppu.WaitVBlank();
        return Hardware.LY;
    }
}
