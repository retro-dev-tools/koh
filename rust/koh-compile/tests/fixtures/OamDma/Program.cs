using Koh.GameBoy;

public static unsafe class Program
{
    [KohAligned(256)]
    private static byte[] Source = new byte[160];

    [KohIntrinsic("oamdma")]
    private static void Alias(byte page) => throw null;

    private static void Transfer(byte page) => Hardware.RunOamDma(page);

    public static int Raw()
    {
        Hardware.LCDC = 0;
        for (int i = 0; i < Source.Length; i++)
            Source[i] = (byte)(i * 3 + 1);
        fixed (byte* pointer = &Source[0])
            Hardware.DMA = (byte)((ushort)pointer >> 8);
        return 42;
    }

    public static int Main()
    {
        Hardware.LCDC = 0;
        *(byte*)0xff70 = 2;
        for (int i = 0; i < Source.Length; i++)
            Source[i] = (byte)(i * 3 + 1);
        fixed (byte* pointer = &Source[0])
        {
            byte page = (byte)((ushort)pointer >> 8);
            Transfer(page);
            if (Gb.Oam[0] != 1 || Gb.Oam[159] != 222)
                return 1;
            for (int i = 0; i < Source.Length; i++)
                Source[i] ^= 255;
            Alias(page);
            if (Gb.Oam[0] != 254 || Gb.Oam[159] != 33)
                return 2;
        }
        byte bank = *(byte*)0xff70;
        return bank == 255 || (bank & 7) == 2 ? 42 : 3;
    }
}
