using Koh.GameBoy;

public static unsafe class Program
{
    public static int Main()
    {
        bool color = Cgb.IsColor();
        Cgb.SelectVramBank(1);
        Cgb.SetBackgroundColor(0, 0, 0x1234);
        Cgb.TryEnableDoubleSpeed();
        Hardware.LCDC = 0x80;
        Ppu.WaitForVramAccess();
        Ppu.WaitForHBlank();
        Ppu.WaitVBlank();
        Hardware.LCDC = 0;
        return color ? 1 : 0;
    }

    public static int Bank()
    {
        Cgb.SelectVramBank(1);
        *(byte*)0x8000 = 0x99;
        return 42;
    }

    public static int BankRoundTrip()
    {
        Cgb.SelectVramBank(1);
        *(byte*)0x8000 = 0x99;
        Cgb.SelectVramBank(0);
        *(byte*)0x8000 = 0x55;
        Cgb.SelectVramBank(1);
        return 42;
    }

    public static int Palette0()
    {
        Cgb.SetBackgroundColor(0, 0, 0x1234);
        return 42;
    }

    public static int Palette23()
    {
        Cgb.SetBackgroundColor(2, 3, 0x7fff);
        return 42;
    }

    public static int PaletteAll()
    {
        for (byte palette = 0; palette < 8; palette++)
        for (byte slot = 0; slot < 4; slot++)
            Cgb.SetBackgroundColor(palette, slot, (ushort)((palette * 4 + slot) * 0x421));
        return 42;
    }
}
