using Koh.GameBoy;
using Koh.GameBoy.Graphics;

public static unsafe class Program
{
    private static string Cached = "SCORE";

    private static string Echo(string value) => value;

    private static string Recurse(string value, int depth) =>
        depth == 0 ? value : Recurse(value, depth - 1);

    private static string Other() => "SCORE";

    private static int Length(string value) => value.Length;

    private static char At(string value, int index) => value[index];

    private static bool Same(string left, string right) => (object)left == (object)right;

    private static void Update(ref int value) => value = 9;

    private static void WriteAscii(byte* destination, string value)
    {
        for (int i = 0; i < value.Length; i++)
            destination[i] = (byte)value[i];
    }

    public static int Main()
    {
        *(byte*)0xff70 = 2;
        string value = Echo(Cached);
        int sum = 0;
        foreach (char unit in value)
            sum += unit;
        string units = Echo("\0caf\u00e9\uD83D\uDE00\uD800x\uDC00");
        if (sum != 380 || Length(value) != 5 || At(value, 4) != 'E')
            return 1;
        if (units.Length != 10 || units[0] != 0 || units[4] != 233)
            return 2;
        if (units[5] != 0xd83d || units[6] != 0xde00 || units[7] != 0xd800 || units[9] != 0xdc00)
            return 3;
        if (!Same(value, Other()) || Same(value, Echo("OTHER")))
            return 4;
        if (!Same(value, Recurse(value, 3)))
            return 10;
        Hardware.BGP = 0;
        string selected = Hardware.BGP == 0 ? null : value;
        if (selected != null || Echo(null) != null || Length(Echo("")) != 0)
            return 5;
        byte* output = stackalloc byte[5];
        for (int i = 0; i < value.Length; i++)
            output[i] = (byte)value[i];
        Hardware.BGP = output[0];
        if (
            Hardware.BGP != 83
            || output[1] != 67
            || output[2] != 79
            || output[3] != 82
            || output[4] != 69
        )
            return 6;
        byte bank = *(byte*)0xff70;
        if (bank != 255 && (bank & 7) != 2)
            return 7;
        int local = 7;
        Update(ref local);
        if (local != 9)
            return 8;
        int* pointer = &local;
        *pointer = 11;
        if (local != 11)
            return 9;
        return 42;
    }

    public static int ArrayCopies()
    {
        byte[] first = new byte[5];
        byte[] second = new byte[5];
        string value = Other();
        int index = 0;
        foreach (char unit in value)
            first[index++] = (byte)unit;
        fixed (byte* destination = &second[0])
            WriteAscii(destination, value);
        Hardware.BGP = first[0];
        return Hardware.BGP == 83 ? 42 : 1;
    }

    public static int NullLength()
    {
        string value = null;
        _ = value.Length;
        return 42;
    }

    public static int NullIndex() => At(null, int.MinValue);

    public static int Negative() => At("A", -1);

    public static int High() => At("A", 65536);

    public static int End() => At("A", 1);

    public static int Empty() => At("", 0);

    public static string Unsupported() => string.Concat("a", Other());

    public static int TextLibrary()
    {
        Video.Init();
        Text.Draw(1, 2, Other());
        Text.DrawToWindow(2, 3, "HI");
        byte* bg = Gb.TileMap + 65;
        byte* win = Gb.TileMap1 + 98;
        if (bg[0] != 51 || bg[1] != 35 || bg[2] != 47 || bg[3] != 50 || bg[4] != 37)
            return 1;
        return win[0] == 40 && win[1] == 41 ? 42 : 2;
    }
}
