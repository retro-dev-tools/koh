using Koh.GameBoy;
using Koh.GameBoy.Graphics;

public static unsafe class Program
{
    [KohIntrinsic("palettewrite", 0xFF69)]
    private static void DirectBg(byte index, ushort value) { }

    [KohIntrinsic("palettewrite", 0xFF6B)]
    private static void DirectObj(byte index, ushort value) { }

    [KohIntrinsic("palettewrite")]
    private static void MissingPort(byte index, ushort value) { }

    [KohIntrinsic("palettewrite", 0xFF47)]
    private static void WrongPort(byte index, ushort value) { }

    [KohIntrinsic("palettewrite", 0xFF69)]
    private static void WrongIndex(ushort index, ushort value) { }

    [KohIntrinsic("palettewrite", 0xFF69)]
    private static void WrongColor(byte index, int value) { }

    [KohIntrinsic("palettewrite", 0xFF69)]
    private static int WrongReturn(byte index, ushort value) => 0;

    public static int InvalidMissingPort()
    {
        MissingPort(0, 1);
        return 42;
    }

    public static int InvalidPort()
    {
        WrongPort(0, 1);
        return 42;
    }

    public static int InvalidIndexType()
    {
        WrongIndex(0, 1);
        return 42;
    }

    public static int InvalidColorType()
    {
        WrongColor(0, 1);
        return 42;
    }

    public static int InvalidReturn() => WrongReturn(0, 1);

    public static int OddIndex()
    {
        Video.Init();
        DirectBg(1, 0x1234);
        return 42;
    }

    public static int HighIndex()
    {
        Video.Init();
        DirectObj(64, 0x1234);
        return 42;
    }

    public static int DmgOnly()
    {
        Video.Init();
        DirectBg(0, 0x1234);
        return 42;
    }

    public static int DirectBounds()
    {
        Video.Init();
        byte* guard = stackalloc byte[256];
        for (int i = 0; i < 256; i++)
            guard[i] = (byte)(i ^ 0xA5);
        DirectBg(0, 0x1F);
        DirectBg(62, 0x1234);
        DirectObj(0, 0x3E0);
        DirectObj(62, 0x4567);
        for (int i = 0; i < 256; i++)
            if (guard[i] != (byte)(i ^ 0xA5))
                return -1;
        Capture();
        return Finish();
    }

    public static int ArmedRaw()
    {
        Video.Init();
        if (!Cgb.TryEnableDoubleSpeed())
            return -1;
        Hardware.KEY1 = 1;
        DirectBg(0, 0x1F);
        DirectBg(62, 0x1234);
        DirectObj(0, 0x3E0);
        DirectObj(62, 0x4567);
        Capture();
        *(byte*)0xDF04 = Hardware.KEY1;
        return Finish();
    }

    public static int ArmedSpeed()
    {
        Video.Init();
        if (!Cgb.TryEnableDoubleSpeed())
            return -1;
        Hardware.KEY1 = 1;
        if (!Cgb.IsColor())
            return -3;
        SetColorOne();
        Capture();
        *(byte*)0xDF04 = Hardware.KEY1;
        return Finish();
    }

    private static int Finish()
    {
        Video.Start();
        Video.EndFrame();
        return 42;
    }

    private static void Capture()
    {
        if (!Video.IsCgb)
            return;
        byte* p = (byte*)0xDF00;
        p[0] = Hardware.BCPD;
        p[1] = Hardware.OCPD;
        p[2] = (byte)(Hardware.BCPS & 0xBF);
        p[3] = (byte)(Hardware.OCPS & 0xBF);
    }

    public static int Combined()
    {
        Video.Init();
        Palettes.SetBg(0, Rgb.Make(10, 20, 30), Rgb.Red, Rgb.Green, Rgb.Blue, 0x1B);
        Palettes.SetObj(0, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0xC6);
        Capture();
        return Finish();
    }

    public static int ObjectOne()
    {
        Video.Init();
        Palettes.SetObj(1, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0x93);
        Capture();
        return Finish();
    }

    private static void SetColorOne()
    {
        Palettes.SetBg(1, Rgb.White, Rgb.Make(20, 25, 20), Rgb.Make(8, 14, 8), Rgb.Black, 0xE4);
        Palettes.SetObj(1, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0xE4);
    }

    public static int ColorOne()
    {
        Video.Init();
        SetColorOne();
        Capture();
        return Finish();
    }

    public static int OnColorOne()
    {
        Video.Init();
        Video.Start();
        SetColorOne();
        if (Video.IsCgb)
            Ppu.WaitVBlank();
        Capture();
        return Finish();
    }

    public static int HighSlots()
    {
        Video.Init();
        Palettes.SetBg(0, Rgb.White, Rgb.Red, Rgb.Green, Rgb.Blue, 0x1B);
        Palettes.SetObj(0, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0x1B);
        Palettes.SetObj(1, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0x1B);
        Palettes.SetBg(2, Rgb.White, Rgb.Red, Rgb.Green, Rgb.Blue, 0xC6);
        Palettes.SetObj(2, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0xC6);
        Capture();
        return Finish();
    }

    public static int LastSlot()
    {
        Video.Init();
        Palettes.SetBg(
            7,
            Rgb.Make(3, 4, 5),
            Rgb.Make(6, 7, 8),
            Rgb.Make(9, 10, 11),
            Rgb.Make(12, 13, 14),
            0x5A
        );
        Palettes.SetObj(7, Rgb.Red, Rgb.Green, Rgb.Blue, 0x1234, 0xA5);
        Capture();
        return Finish();
    }

    public static int Packed()
    {
        Video.Init();
        ushort* p = (ushort*)0xDF00;
        for (int i = 0; i < 32; i++)
            p[i] = Rgb.Make((byte)(i * 9), (byte)(i * 17), (byte)(255 - i * 3));
        p[32] = Rgb.Make(31, 20, 5);
        Hardware.SCX = (byte)p[32];
        Hardware.SCY = (byte)(p[32] >> 8);
        return Finish();
    }

    public static int Sweep()
    {
        Video.Init();
        Video.Start();
        for (byte i = 0; i < 32; i++)
        {
            byte slot = (byte)(i & 7);
            Hardware.EnableInterrupts();
            Palettes.SetBg(
                slot,
                Rgb.Make(i, 1, 2),
                Rgb.Make(3, i, 4),
                Rgb.Make(5, 6, i),
                Rgb.Make(i, i, i),
                0xE4
            );
            Hardware.EnableInterrupts();
            Palettes.SetObj(
                slot,
                Rgb.Make((byte)(31 - i), i, 0),
                Rgb.Make(0, (byte)(31 - i), i),
                Rgb.Make(i, 0, (byte)(31 - i)),
                Rgb.Make(i, (byte)(31 - i), i),
                0xE4
            );
        }
        Ppu.WaitVBlank();
        Capture();
        return Finish();
    }
}
