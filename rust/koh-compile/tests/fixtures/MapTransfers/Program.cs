using Koh.GameBoy.Graphics;

public static unsafe class Program
{
    public static int Fill()
    {
        Video.Init();
        *(byte*)0xC010 = 1;
        Video.Start();
        *(byte*)0xC010 = 2;
        Bg.Fill(0, 0, 32, 32, 0x33);
        *(byte*)0xC010 = 3;
        Win.Fill(0, 0, 32, 32, 0x11);
        *(byte*)0xC010 = 4;
        Video.EndFrame();
        *(byte*)0xC010 = 5;
        return 42;
    }

    public static int Clear()
    {
        Video.Init();
        *(byte*)0xC010 = 1;
        Video.Start();
        *(byte*)0xC010 = 2;
        Bg.Clear(0x33);
        *(byte*)0xC010 = 3;
        Win.Fill(0, 0, 32, 32, 0x11);
        *(byte*)0xC010 = 4;
        Video.EndFrame();
        *(byte*)0xC010 = 5;
        return 42;
    }
}
