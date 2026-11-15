using Koh.GameBoy;
using Koh.GameBoy.Graphics;

public static unsafe class Program
{
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
