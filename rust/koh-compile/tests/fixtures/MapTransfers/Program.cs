using Koh.GameBoy;
using Koh.GameBoy.Graphics;

public static unsafe class Program
{
    public static int CommitBackground() => Commit(false);

    public static int CommitWindow() => Commit(true);

    private static int Commit(bool window)
    {
        Video.Init();
        Video.Start();
        if (window)
        {
            Win.SetTile(2, 3, 7);
            Win.SetTile(19, 3, 9);
            Win.SetTile(5, 10, 4);
        }
        else
        {
            Bg.SetTile(2, 3, 7);
            Bg.SetTile(19, 3, 9);
            Bg.SetTile(5, 10, 4);
        }
        for (byte frame = 1; frame <= 61; frame++)
        {
            Video.EndFrame();
            Hardware.SCX = frame;
        }
        Hardware.SCX = 62;
        Video.EndFrame();
        Hardware.SCX = 63;
        return 42;
    }

    public static int BankedFrame()
    {
        Video.Init();
        Video.Start();
        *(byte*)0xFF70 = 2;
        Bg.SetTile(0, 0, 42);
        Win.SetTile(31, 31, 77);
        Video.EndFrame();
        *(byte*)0xDF00 = 0xA3;
        Hardware.BGP = (byte)(*(byte*)0xFF70 & 7);
        return 42;
    }

    public static int CanvasPixels()
    {
        Video.Init();
        Canvas.Init(2, 2, CanvasMode.SingleBuffered);
        Canvas.Clear(0);
        Canvas.SetPixel(0, 0, 3);
        Canvas.SetPixel(8, 3, 2);
        Canvas.SetPixel(15, 15, 1);
        Canvas.Present();
        return 42;
    }

    public static int CanvasRect()
    {
        Video.Init();
        Canvas.Init(1, 1, CanvasMode.SingleBuffered);
        Canvas.FillRect(0, 0, 8, 8, 6);
        Canvas.Present();
        return 42;
    }

    public static int CanvasDither()
    {
        Video.Init();
        Canvas.Init(1, 1, CanvasMode.SingleBuffered);
        Canvas.Clear(0);
        Canvas.FillSpan(0, 0, 7, 3);
        Canvas.FillSpan(1, 0, 7, 3);
        Canvas.Present();
        return 42;
    }

    public static int CanvasLine()
    {
        Video.Init();
        Canvas.Init(2, 2, CanvasMode.SingleBuffered);
        Video.Start();
        Canvas.Clear(0);
        Canvas.DrawLine(0, 0, 15, 15, 3);
        Canvas.Present();
        return 42;
    }

    public static int CanvasClippedLine()
    {
        Video.Init();
        Canvas.Init(2, 2, CanvasMode.SingleBuffered);
        Video.Start();
        Canvas.Clear(0);
        Canvas.DrawLine(-3, -3, 18, 18, 3);
        Canvas.Present();
        return 42;
    }

    public static int CanvasTriangle()
    {
        Video.Init();
        Canvas.Init(1, 1, CanvasMode.SingleBuffered);
        Video.Start();
        Canvas.Clear(0);
        Canvas.FillTriangle(0, 0, 7, 0, 7, 7, 6);
        Canvas.Present();
        return 42;
    }

    public static int CanvasDouble()
    {
        Video.Init();
        Canvas.Init(1, 1, CanvasMode.DoubleBuffered);
        Video.Start();
        Canvas.Clear(0);
        Canvas.SetPixel(0, 0, 3);
        Canvas.Present();
        *(byte*)0xDF00 = Hardware.LCDC;
        Canvas.Clear(0);
        Canvas.SetPixel(1, 0, 3);
        Canvas.Present();
        *(byte*)0xDF01 = Hardware.LCDC;
        return 42;
    }

    public static int CanvasCgb()
    {
        Video.Init();
        Canvas.Init(1, 1, CanvasMode.SingleBuffered);
        Video.Start();
        Canvas.Clear(0);
        Canvas.SetPixel(0, 0, 3);
        Canvas.Present();
        return 42;
    }

    private static void StartFont()
    {
        Video.Init();
        Font.LoadDefault(0);
    }

    public static int FontScore()
    {
        StartFont();
        Text.Draw(1, 0, "SCORE");
        return 42;
    }

    public static int FontWindow()
    {
        StartFont();
        Text.DrawToWindow(2, 3, "HI");
        return 42;
    }

    public static int FontRight()
    {
        StartFont();
        Text.DrawNumber(0, 0, 1234, 5);
        return 42;
    }

    public static int FontLeft()
    {
        StartFont();
        Text.DrawNumber(0, 0, 42);
        Text.DrawNumber(0, 1, 0);
        return 42;
    }

    public static int FontBytes()
    {
        StartFont();
        Text.Draw(4, 2, new byte[] { (byte)'H', (byte)'I', (byte)'!' }, 3);
        return 42;
    }

    public static int FontOffset()
    {
        Video.Init();
        Font.LoadDefault(7);
        Text.Draw(1, 0, "SCORE");
        Text.DrawNumber(0, 1, 65535, 3);
        Text.DrawNumberToWindow(0, 2, 0, 3);
        return 42;
    }

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
