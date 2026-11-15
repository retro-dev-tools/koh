using Koh.GameBoy;

public static class Program
{
    static readonly byte[] Template = { 21, 22, 23, 24 };

    static Program() { }

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
        return (Bank & 7) == 2 ? result : 9;
    }

    public static int BankedProbe()
    {
        Bank = 2;
        int result = Probe();
        return (Bank & 7) == 2 ? result : 9;
    }

    public static int Probe()
    {
        byte[] value = { 1, 2, 3 };
        return value.Length;
    }

    public static int Main()
    {
        byte[] first = Data.Make();
        byte[] second = Data.Make();
        first[1] = 99;
        Template[2] = 77;
        if (first == second || second[1] != 2 || Template[0] != 21 || Types() != 0)
            return 1;
        return first.Length + second.Length + second[0];
    }

    static int Types()
    {
        sbyte[] signed = { -1, 2, -3 };
        short[] shorts = { -1, 0x1234, -1234 };
        ushort[] words = { 0xFEDC, 1, 2 };
        int[] integers = { -1, 0x12345678, int.MinValue };
        uint[] unsigned = { 0xFEDCBA98, 1, uint.MaxValue };
        long[] wide = { 0x1122334455667788, -2, long.MinValue };
        ulong[] uwide = { 0xFEDCBA9876543210, 1, ulong.MaxValue };
        char[] characters = { 'A', '\uF123', '\0' };
        bool[] flags = { true, false, true, false };
        return
            signed[2] == -3
            && shorts[2] == -1234
            && words[0] == 0xFEDC
            && integers[2] == int.MinValue
            && unsigned[0] == 0xFEDCBA98
            && wide[1] == -2
            && uwide[0] == 0xFEDCBA9876543210
            && characters[1] == '\uF123'
            && flags[0]
            && !flags[1]
            ? 0
            : 2;
    }
}
