using System;
using Koh.GameBoy;
using Koh.GameBoy.Graphics;

public static unsafe class Program
{
    private static int Finish()
    {
        Video.Start();
        Video.EndFrame();
        return 42;
    }

    public static int Mutate()
    {
        Video.Init();
        Sprites.Get(0, out Sprite a);
        a.Set(16, 32, 5);
        a.SetAttr(ObjAttr.FlipX);
        Sprites.Get(1, out Sprite b);
        b.Set(-4, -20, 9);
        Sprites.Get(2, out Sprite c);
        c.Set(50, 50, 3);
        c.Hide();
        return Finish();
    }

    public static int HideAll()
    {
        Video.Init();
        for (byte i = 0; i < Sprites.Count; i++)
        {
            Sprites.Get(i, out Sprite sprite);
            sprite.Set(i, i + 1, i);
            sprite.SetAttr(0xF0);
        }
        Sprites.HideAll();
        return Finish();
    }

    public static int Move()
    {
        Video.Init();
        Sprites.Get(3, out Sprite sprite);
        sprite.Set(10, 10, 42);
        sprite.SetAttr(ObjAttr.Priority);
        sprite.Move(20, 30);
        return Finish();
    }

    public static int NoMutation()
    {
        Video.Init();
        return Finish();
    }

    public static int CleanFrame()
    {
        Video.Init();
        Video.Start();
        Video.EndFrame();
        Video.EndFrame();
        *(byte*)0xDF00 = Video.FrameCount;
        return 42;
    }

    public static int Tile()
    {
        Video.Init();
        Sprites.Get(0, out Sprite sprite);
        sprite.Set(0, 0, 5);
        sprite.SetTile(255);
        sprite.SetAttr((byte)(ObjAttr.FlipY | ObjAttr.DmgPalette1));
        return Finish();
    }

    public static int ColorPalette()
    {
        Video.Init();
        Palettes.SetObj(0, 0x001F, 0x03E0, 0x7C00, 0x1234, 0xE4);
        Palettes.SetObj(1, 0x7FFF, 0x0421, 0x294A, 0, 0x1B);
        Palettes.SetObj(7, 0x0001, 0x0002, 0x0003, 0x0004, 0xFF);
        Sprites.Get(0, out Sprite sprite);
        sprite.Set(20, 30, 5);
        sprite.SetAttr(ObjAttr.CgbPalette(0));
        Sprites.Get(39, out Sprite last);
        last.Set(100, 80, 17);
        last.SetAttr((byte)(ObjAttr.CgbPalette(15) | ObjAttr.FlipY));
        return Finish();
    }

    public static int Registers()
    {
        Video.Init();
        Hardware.LYC = 0x5A;
        Hardware.OBP0 = 0xE4;
        Hardware.OBP1 = 0x1B;
        if (Video.IsCgb)
        {
            Hardware.OCPS = 0;
            Hardware.OCPD = 0x2C;
            *(byte*)0xDF00 = (byte)(Hardware.OCPS & 0xBF);
            *(byte*)0xDF01 = Hardware.OCPD;
        }
        int result = Finish();
        Video.Stop();
        return result;
    }

    public static string HostDma()
    {
        for (int i = 0; i < 160; i++)
        {
            Gb.Wram[i] = (byte)(i + 1);
            Gb.Oam[i] = 0xC9;
        }
        Hardware.DMA = 0xC0;
        byte[] result = new byte[160];
        for (int i = 0; i < result.Length; i++)
            result[i] = Gb.Oam[i];
        return Convert.ToHexString(result);
    }

    public static int UnsafePalette()
    {
        Video.Init();
        Video.Start();
        for (int i = 0; i < 10000; i++)
            Hardware.OCPD = 0x55;
        return 42;
    }
}
