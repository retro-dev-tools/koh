using System;

public static class Program
{
    public static byte[] Result = null!;
    private static UInt128 Stored;

    private static UInt128 Wide(ulong high, ulong low) => ((UInt128)high << 64) | low;

    private static int Save(UInt128 value)
    {
        Result = new byte[16];
        for (int i = 0; i < Result.Length; i++)
            Result[i] = (byte)(value >> (i * 8));
        return 42;
    }

    public static int Carry() => Save(Wide(0x123456789abcdef0, ulong.MaxValue) + 1);

    public static int Borrow() => Save(Wide(0x123456789abcdef0, 0) - 1);

    public static int SignExtend() => Save(unchecked((UInt128)(Int128)(-42L)));

    public static int Multiply() => Save(Wide(0x12345678, 0xfedcba9876543210) * 1000);

    public static int Divide() => Save(Wide(0x12345678, 0xfedcba9876543210) / 1000);

    public static int Remainder() => Save(Wide(0x12345678, 0xfedcba9876543210) % 1000);

    public static int SignedDivide() =>
        Save(unchecked((UInt128)(-(Int128)Wide(0x12345678, 0xfedcba9876543210) / 1000)));

    public static int SignedRemainder() =>
        Save(unchecked((UInt128)(-(Int128)Wide(0x12345678, 0xfedcba9876543210) % 1000)));

    public static int Complement() => Save(~Wide(0x12345678, 0xfedcba9876543210));

    public static int ShiftMask() => Save(Wide(0x12345678, 0xfedcba9876543210) << -128);

    public static int HighShift() => Save(Wide(0x12345678, 0xfedcba9876543210) >> 64);

    public static int SignedShift() =>
        Save(unchecked((UInt128)(-(Int128)Wide(0x12345678, 0xfedcba9876543210) >> 127)));

    public static int Compare()
    {
        UInt128 a = Wide(0x8000000000000000, 0);
        UInt128 b = Wide(0x7fffffffffffffff, ulong.MaxValue);
        int flags = 0;
        if (a > b)
            flags |= 1;
        if (b < a)
            flags |= 2;
        if (a != b)
            flags |= 4;
        if (a == b)
            flags |= 8;
        if (a >= Wide(0x8000000000000000, 0))
            flags |= 16;
        if (b <= Wide(0x7fffffffffffffff, ulong.MaxValue))
            flags |= 32;
        if (unchecked((Int128)a) < unchecked((Int128)b))
            flags |= 64;
        return Save((UInt128)flags);
    }

    private static UInt128 Recur(UInt128 value, int depth) =>
        depth == 0 ? value : Recur(value + 1, depth - 1);

    public static int Recursive() => Save(Recur(Wide(0x123456789abcdef0, ulong.MaxValue), 3));

    public static int StaticField()
    {
        Stored = Wide(0x12345678, 0xfedcba9876543210);
        return Save(Stored);
    }

    public static int LegacyRoundTrip()
    {
        Int128 a = 123456789012345678;
        Int128 b = 987654321098765432;
        Int128 sum = a + b;
        Int128 diff = sum - b;
        Int128 product = a * 1000;
        UInt128 shifted = (UInt128)250 << 4;
        if (diff != a || product / 1000 != a || a >= b || shifted != 4000)
            return 0;
        return Save((UInt128)sum);
    }

    private static Int128 FloatToWide(double value) => (Int128)value;

    public static int FloatingConversion() => Save((UInt128)FloatToWide(1.5));

    private static UInt128 CheckedAdd(UInt128 value) => checked(value + 1);

    public static int CheckedAddition() => Save(CheckedAdd(Wide(0, 42)));

    public static int Main() => Carry();
}
