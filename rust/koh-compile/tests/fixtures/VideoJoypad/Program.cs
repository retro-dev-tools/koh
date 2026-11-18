using Koh.GameBoy;
using Koh.GameBoy.Graphics;

public static unsafe class Program
{
    public static int Composition()
    {
        Video.Init();
        Video.ShowSprites(SpriteSize.Size8x16);
        Video.ShowWindow(24, 40);
        Video.Start();
        return 42;
    }

    public static int LayerTransitions()
    {
        Composition();
        byte* p = (byte*)0xDF00;
        p[0] = Hardware.LCDC;
        Video.HideSprites();
        p[1] = Hardware.LCDC;
        Video.HideWindow();
        p[2] = Hardware.LCDC;
        Video.Stop();
        p[3] = Hardware.LCDC;
        Video.ShowSprites(SpriteSize.Size8x8);
        p[4] = Hardware.LCDC;
        Video.ShowWindow(0, 143);
        p[5] = Hardware.LCDC;
        Video.Start();
        p[6] = Hardware.LCDC;
        return 42;
    }

    public static int Frames()
    {
        Video.Init();
        Video.Start();
        byte* p = (byte*)0xDF00;
        Video.EndFrame();
        Hardware.SCX = Video.FrameCount;
        p[0] = Video.FrameCount;
        p[1] = Hardware.LY;
        Video.EndFrame();
        Hardware.SCX = Video.FrameCount;
        p[2] = Video.FrameCount;
        p[3] = Hardware.LY;
        Video.FrameCount = 255;
        Video.EndFrame();
        Hardware.SCX = Video.FrameCount;
        p[4] = Video.FrameCount;
        p[5] = Hardware.LY;
        return 42;
    }

    public static int Held()
    {
        byte* p = (byte*)0xDF00;
        p[0] = Joypad.Pressed();
        p[1] = Joypad.Pressed();
        Hardware.SCX = 1;
        p[2] = Joypad.ReadAll();
        p[3] = Joypad.Pressed();
        Hardware.SCX = 2;
        p[4] = Joypad.Pressed();
        p[5] = Joypad.Pressed();
        p[6] = Joypad.ReadAll();
        p[7] = Joypad.IsPressed(p[6], Button.Left) ? (byte)1 : (byte)0;
        Hardware.SCX = p[0];
        Hardware.SCY = p[1];
        p[8] = Hardware.SCX;
        p[9] = Hardware.SCY;
        return 42;
    }
}
