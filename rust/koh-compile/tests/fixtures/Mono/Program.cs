using Koh.GameBoy;
using Koh.GameBoy.Graphics;

public static class Program
{
    private static readonly byte[] Data =
    {
        0x00,
        0xFF,
        0x80,
        0x01,
        0x55,
        0xAA,
        0x3C,
        0xC3,
        0x0F,
        0xF0,
        0x12,
        0x34,
        0x56,
        0x78,
        0x9A,
        0xBC,
    };

    [KohIntrinsic("register", 0xFF70)]
    private static byte Bank
    {
        get => 0;
        set { }
    }

    public static int Main() => Matrix(false, false, false);

    public static int On() => Matrix(true, false, false);

    public static int BankOne() => Matrix(true, true, false);

    public static int Wrap() => Matrix(true, false, true);

    public static int Empty()
    {
        Prepare(false, false);
        TileSet.Load1bpp(0, null!, 3, 0, 0);
        return Result();
    }

    private static void Prepare(bool on, bool bankOne)
    {
        if ((Hardware.LCDC & 0x80) != 0)
        {
            Ppu.WaitVBlank();
            Hardware.LCDC = 0;
        }
        Bank = 2;
        Cgb.SelectVramBank(bankOne ? (byte)1 : (byte)0);
        if (on)
            Hardware.LCDC = 0x91;
    }

    private static int Result() => !Cgb.IsColor() || (Bank & 7) == 2 ? 42 : -1;

    private static int Matrix(bool on, bool bankOne, bool wrap)
    {
        Prepare(on, bankOne);
        if (wrap)
        {
            TileSet.Load1bpp(255, Data, 1, 2, 2);
        }
        else
        {
            for (byte ink = 0; ink < 4; ink++)
            for (byte paper = 0; paper < 4; paper++)
                TileSet.Load1bpp((byte)(16 + (ink * 4 + paper) * 2), Data, ink, paper, 2);
        }
        return Result();
    }
}
