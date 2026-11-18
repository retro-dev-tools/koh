using Koh.GameBoy;
using Koh.GameBoy.Graphics;

public static unsafe class Program
{
    public static int BackgroundContract()
    {
        Video.Init();
        Bg.Fill(2, 3, 4, 2, 7);
        Bg.DrawMap(10, 1, 3, 2, new byte[] { 10, 11, 12, 13, 14, 15 });
        Bg.SetTile(0, 0, 42);
        return 42;
    }

    public static int WindowContract()
    {
        Video.Init();
        Win.Fill(5, 6, 3, 2, 9);
        Win.DrawMap(0, 0, 2, 2, new byte[] { 20, 21, 22, 23 });
        Win.SetTile(15, 15, 77);
        return 42;
    }

    public static int AttributeContract()
    {
        Video.Init();
        Bg.SetAttr(5, 5, (byte)(TileAttr.FlipX | TileAttr.Palette(3)));
        Bg.FillAttr(0, 0, 2, 2, TileAttr.Priority);
        Win.SetAttr(1, 1, TileAttr.FlipY);
        return Hardware.VBK == (Video.IsCgb ? 0xFE : 0xFF) ? 42 : 1;
    }

    public static int BoardContract()
    {
        Video.Init();
        Video.Start();
        for (int i = 0; i < 5; i++)
            Video.EndFrame();
        for (byte row = 0; row < 4; row++)
        for (byte column = 0; column < 4; column++)
            Bg.Fill((byte)(3 + column * 4), (byte)(3 + row * 3), 2, 2, 7);
        for (int i = 0; i < 120; i++)
            Video.EndFrame();
        return 42;
    }

    public static int LazyBg() => Lazy(false);

    public static int LazyWindow() => Lazy(true);

    public static int BadBgIndex() => BadIndex(false);

    public static int BadWindowIndex() => BadIndex(true);

    private static int BadIndex(bool window)
    {
        Video.Init();
        if (window)
            Win.SetTile(255, 255, 0);
        else
            Bg.SetTile(255, 255, 0);
        return 42;
    }

    private static int Lazy(bool window)
    {
        Video.Init();
        byte* raw = Mem.Alloc(2500);
        raw[0] = 42;
        raw[2499] = 43;
        Bg.SetTile(0, 0, 0);
        Win.SetTile(0, 0, 0);
        Bg.Clear(0x33);
        Video.Start();
        if (window)
            Win.SetTile(3, 2, 0x11);
        else
            Bg.SetTile(3, 2, 0x44);
        Video.EndFrame();
        return raw[0] == 42 && raw[2499] == 43 ? 42 : 1;
    }

    public static int Fill()
    {
        Video.Init();
        Video.Start();
        Bg.Fill(0, 0, 32, 32, 0x33);
        Win.Fill(0, 0, 32, 32, 0x11);
        Video.EndFrame();
        return 42;
    }

    public static int Clear()
    {
        Video.Init();
        Video.Start();
        Bg.Clear(0x33);
        Win.Fill(0, 0, 32, 32, 0x11);
        Video.EndFrame();
        return 42;
    }
}
