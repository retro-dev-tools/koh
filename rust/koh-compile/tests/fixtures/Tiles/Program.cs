using Koh.GameBoy;
using Koh.GameBoy.Graphics;

public static unsafe class Program
{
    [KohIntrinsic("register", 0xFF70)]
    static byte Bank
    {
        get => 0;
        set { }
    }

    public static int Aligned() => Load(true);

    public static int OnAligned() => Load(true, true);

    public static int OnUnaligned() => Load(false, true);

    public static int BankedOnLargeAligned()
    {
        Bank = 2;
        Hardware.LCDC = 0;
        Video.IsCgb = Cgb.IsColor();
        byte[] padding = new byte[12];
        padding[0] = 42;
        byte[] tiles = new byte[1920];
        for (int i = 0; i < tiles.Length; i++)
            tiles[i] = (byte)i;
        Hardware.LCDC = 0x91;
        TileSet.Load(3, tiles, 120);
        return (Bank & 7) == 2 ? 0x43 : -1;
    }

    public static int BankedOnAligned()
    {
        Bank = 2;
        int result = OnAligned();
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int BankedOnUnaligned()
    {
        Bank = 2;
        int result = OnUnaligned();
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int VideoInit()
    {
        Video.Init();
        return Video.IsCgb ? 0x43 : 0x44;
    }

    public static int BankedVideoInit()
    {
        Bank = 2;
        int result = VideoInit();
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int Unaligned() => Load(false);

    public static int BankedAligned()
    {
        Bank = 2;
        int result = Load(true);
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int BankedUnaligned()
    {
        Bank = 2;
        int result = Load(false);
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int BankedRender()
    {
        Bank = 2;
        int result = Render();
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int Render()
    {
        Hardware.LCDC = 0;
        Video.IsCgb = Cgb.IsColor();
        byte[] tiles =
        {
            0xAA,
            0xCC,
            0x55,
            0x33,
            0xAA,
            0xCC,
            0x55,
            0x33,
            0xAA,
            0xCC,
            0x55,
            0x33,
            0xAA,
            0xCC,
            0x55,
            0x33,
        };
        TileSet.Load(0, tiles, 1);
        Mem.Fill(Gb.TileMap, 0, 1024);
        if (Video.IsCgb)
        {
            Cgb.SelectVramBank(1);
            Mem.Fill(Gb.TileMap, 0, 1024);
            Cgb.SelectVramBank(0);
            Cgb.SetBackgroundColor(0, 0, 0x7FFF);
            Cgb.SetBackgroundColor(0, 1, 0x001F);
            Cgb.SetBackgroundColor(0, 2, 0x03E0);
            Cgb.SetBackgroundColor(0, 3, 0x7C00);
        }
        Hardware.BGP = 0xE4;
        Hardware.SCX = 0;
        Hardware.SCY = 0;
        Hardware.LCDC = 0x91;
        return Video.IsCgb ? 0x43 : 0x44;
    }

    static int Load(bool aligned, bool lcdOn = false)
    {
        Hardware.LCDC = 0;
        Video.IsCgb = Cgb.IsColor();
        if (aligned)
        {
            byte[] padding = new byte[12];
            padding[0] = 42;
        }
        byte[] tiles =
        {
            0x3C,
            0x7E,
            0x42,
            0x42,
            0x42,
            0x42,
            0x42,
            0x42,
            0x7E,
            0x5E,
            0x7E,
            0x0A,
            0x7C,
            0x56,
            0x38,
            0x7C,
            0xFF,
            0x00,
            0x7E,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x7E,
            0xFF,
        };
        if (lcdOn)
        {
            for (int i = 0; i < tiles.Length; i++)
                tiles[i] = (byte)(i + 1);
            Hardware.LCDC = 0x91;
        }
        TileSet.Load(lcdOn ? (byte)7 : (byte)3, tiles, 2);
        return Video.IsCgb ? 0x43 : 0x44;
    }
}
