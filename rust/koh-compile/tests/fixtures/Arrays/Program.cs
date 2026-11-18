using Koh.GameBoy;

public static class Program
{
    static byte[] Saved;

    [KohIntrinsic("register", 0xFF70)]
    static byte Bank
    {
        get => 0;
        set { }
    }

    static void Increment(ref int value) => value++;

    static int[] Identity(int[] value) => value;

    static int[] Maybe(bool choose, int[] value) => choose ? value : null;

    static ref int Element(int[] value) => ref value[2];

    static int[] Loop(int[] value)
    {
        int[] result = null;
        for (int i = 0; i < 2; i++)
            result = i == 0 ? null : value;
        return result;
    }

    static int BadSize() => -1;

    static int BadIndex() => -1;

    public static int Main() => Check();

    public static int Banked()
    {
        Bank = 2;
        int result = Check();
        if ((Bank & 7) != 2)
            return 9;
        return result;
    }

    public static int BankedTypes()
    {
        Bank = 2;
        int result = Types();
        return (Bank & 7) == 2 ? result : 9;
    }

    public static int Types()
    {
        bool[] booleans = new bool[1];
        char[] characters = new char[1];
        short[] shorts = new short[1];
        uint[] unsigned = new uint[1];
        ulong[] longs = new ulong[1];
        nint[] natives = new nint[1];
        nuint[] unatives = new nuint[1];
        if (
            booleans[0]
            || characters[0] != 0
            || shorts[0] != 0
            || unsigned[0] != 0
            || longs[0] != 0
            || natives[0] != 0
            || unatives[0] != 0
        )
            return 1;
        booleans[0] = true;
        characters[0] = '\uF123';
        shorts[0] = -1234;
        unsigned[0] = 0xFEDCBA98;
        longs[0] = 0xFEDCBA9876543210;
        natives[0] = -1234;
        unatives[0] = 0xFEDC;
        if (
            !booleans[0]
            || characters[0] != '\uF123'
            || shorts[0] != -1234
            || unsigned[0] != 0xFEDCBA98
            || longs[0] != 0xFEDCBA9876543210
            || natives[0] != -1234
            || unatives[0] != 0xFEDC
        )
            return 2;
        return 42;
    }

    public static int FloatArray() => new float[2].Length;

    public static int ObjectArray() => new object[2].Length;

    public static int NestedArray() => new int[2][].Length;

    public static int Multidimensional() => new int[2, 2].Length;

    public static int RankThree() => new int[2, 2, 2].Length;

    static int Check()
    {
        if (Saved != null)
            return 1;
        int[] values = new int[5];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] != 0)
                return 2;
            values[i] = i + 38;
        }
        ref int element = ref Element(values);
        Increment(ref element);
        int[] alias = Identity(values);
        alias[4] = 42;
        if (values[2] != 41 || alias != values)
            return 3;
        Saved = new byte[3];
        sbyte[] signed = new sbyte[2];
        ushort[] words = new ushort[2];
        long[] wide = new long[2];
        if (Saved[0] != 0 || signed[1] != 0 || words[1] != 0 || wide[1] != 0)
            return 4;
        Saved[1] = 0xAB;
        signed[1] = -2;
        words[1] = 0xFEDC;
        wide[1] = 0x1122334455667788;
        if (
            Saved[1] != 0xAB
            || signed[1] != -2
            || words[1] != 0xFEDC
            || wide[1] != 0x1122334455667788
        )
            return 5;
        byte[] first = new byte[0];
        byte[] second = new byte[0];
        if (first == null || first == second || first.Length != 0)
            return 6;
        if (
            Maybe(true, values).Length != 5
            || Maybe(false, values) != null
            || Loop(values) != values
        )
            return 7;
        return values[4];
    }

    public static int NullArray()
    {
        int[] missing = null;
        return missing.Length;
    }

    public static int Negative() => new int[BadSize()].Length;

    public static int TooLarge() => new byte[4095].Length;

    public static int Truncated() => new byte[65536].Length;

    public static int NegativeIndex() => (new int[2])[BadIndex()];

    public static int HighIndex() => (new byte[2])[65536];

    public static int EmptyIndex() => (new byte[0])[0];

    public static int Exhausted()
    {
        byte[] full = new byte[4094];
        full[0] = 0x7B;
        byte[] another = new byte[0];
        return full[0] + another.Length;
    }
}
