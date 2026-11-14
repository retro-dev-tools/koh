public static class Program
{
    static int Empty;
    static byte Byte;
    static sbyte Signed;
    static ushort Word;
    static short Short;
    static uint Dword;
    static long Wide;
    static char Glyph;
    static bool Flag;
    static nuint Address;

    static void Bump(ref int value) => value++;

    static void WriteByte(ref byte value) => value = 0x34;

    static ref int Alias() => ref Counter.Value;

    public static int NeedsInitialization() => Initialized.Value;

    public static int NeedsGenericStorage() => Holder<int>.Value;

    public static int Main()
    {
        if (Empty != 0 || Wide != 0 || Byte != 0 || Counter.Value != 0)
            return 1;
        if (
            Signed != 0
            || Word != 0
            || Short != 0
            || Dword != 0
            || Glyph != '\0'
            || Flag
            || Address != 0
        )
            return 7;
        Empty = 0;
        Byte = unchecked((byte)0x1AB);
        Signed = -2;
        Word = 0xFEDC;
        Short = -1234;
        Dword = 0x89ABCDEF;
        Wide = unchecked((long)0xFEDCBA9876543210UL);
        Glyph = '\uABCD';
        Flag = true;
        Address = 0xFEDC;
        if (Byte != 0xAB || Signed != -2 || Word != 0xFEDC || Short != -1234)
            return 2;
        if (Dword != 0x89ABCDEF || Wide != unchecked((long)0xFEDCBA9876543210UL))
            return 3;
        if (Glyph != '\uABCD' || !Flag || Address != 0xFEDC)
            return 4;
        WriteByte(ref Byte);
        if (Byte != 0x34)
            return 6;
        Counter.Value = 40;
        Bump(ref Counter.Value);
        ref int alias = ref Alias();
        alias++;
        if (alias != 42)
            return 5;
        return Counter.Read();
    }

    public static long A0,
        A1,
        A2,
        A3,
        A4,
        A5,
        A6,
        A7,
        A8,
        A9,
        A10,
        A11,
        A12,
        A13,
        A14,
        A15,
        A16,
        A17,
        A18,
        A19,
        A20,
        A21,
        A22,
        A23,
        A24,
        A25,
        A26,
        A27,
        A28,
        A29,
        A30,
        A31,
        A32;

    public static void TooMany()
    {
        A0 =
            A1 =
            A2 =
            A3 =
            A4 =
            A5 =
            A6 =
            A7 =
            A8 =
            A9 =
            A10 =
            A11 =
            A12 =
            A13 =
            A14 =
            A15 =
            A16 =
            A17 =
            A18 =
            A19 =
            A20 =
            A21 =
            A22 =
            A23 =
            A24 =
            A25 =
            A26 =
            A27 =
            A28 =
            A29 =
            A30 =
            A31 =
            A32 =
                42;
    }
}

public static class Initialized
{
    public static int Value = 17;
}

public static class Holder<T>
{
    public static int Value;
}
