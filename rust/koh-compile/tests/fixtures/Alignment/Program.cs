using Koh.GameBoy;

public static unsafe class Program
{
    [KohIntrinsic("register", 0xFF70)]
    static byte Bank
    {
        get => 0;
        set { }
    }

    public static int Banked()
    {
        Bank = 2;
        int result = Main();
        return (Bank & 7) == 2 ? result : -2;
    }

    static byte[] Pad = new byte[1];

    [KohAligned(256)]
    static byte[] Shadow = new byte[160];

    [KohAligned(16)]
    static ushort[] Words = new ushort[3];

    public static int Main()
    {
        Pad[0] = 42;
        Shadow[0] = 11;
        Shadow[159] = 22;
        Words[0] = 0x1234;
        Words[2] = 0xABCD;
        fixed (byte* p = &Shadow[0])
        fixed (ushort* q = &Words[0])
        {
            if (((ushort)p & 255) != 0 || ((ushort)q & 15) != 0)
                return -1;
            return Pad[0] + p[0] + p[159] + q[0] + q[2];
        }
    }
}
