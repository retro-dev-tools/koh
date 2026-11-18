using Koh.GameBoy;

public static unsafe class Program
{
    [KohIntrinsic("register", 0xFF43)]
    static byte ReadScroll() => 0;

    [KohIntrinsic("register", 0xFF43)]
    static void WriteScroll(byte value) { }

    [KohIntrinsic("region", 0xC020)]
    static byte* Scratch => throw new System.InvalidOperationException();

    public static int Main()
    {
        Hardware.LCDC = 0;
        Hardware.BGP = 0xE4;
        Hardware.SCX = unchecked((byte)0x107);
        WriteScroll(7);
        Hardware.SCY = 11;
        *(uint*)Gb.Vram = 0x78563412;
        Gb.Vram[3] = 0x7A;
        Gb.TileMap[0] = 24;
        Scratch[0] = 42;
        if (Hardware.LCDC != 0)
            return 1;
        if (Hardware.BGP != 0xE4)
            return 2;
        if (*(uint*)Gb.TileData != 0x7A563412)
            return 3;
        if (ReadScroll() != 7 || Scratch[0] != 42)
            return 4;
        byte* end = Gb.Vram + 4;
        if (*(end - 1) != 0x7A)
            return 5;
        return Hardware.SCX + Hardware.SCY + Gb.TileMap[0];
    }
}
