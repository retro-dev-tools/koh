public static class Float64
{
    struct Cell
    {
        public double Value;
    }

    static Cell Snapshot;

    static unsafe ulong Bits(double value) => *(ulong*)&value;

    static unsafe double From(ulong bits) => *(double*)&bits;

    static unsafe int Store(ulong bits)
    {
        *(ulong*)0xdff8 = bits;
        return 42;
    }

    static unsafe float From32(uint bits) => *(float*)&bits;

    static unsafe uint Bits32(float value) => *(uint*)&value;

    static double Widen(float value) => value;

    static float Narrow(double value) => (float)value;

    static double Add(double a, double b) => a + b;

    static double Subtract(double a, double b) => a - b;

    static double Negate(double value) => -value;

    static double Multiply(double a, double b) => a * b;

    static double Divide(double a, double b) => a / b;

    static double Remainder(double a, double b) => a % b;

    static double Convert(long value) => value;

    static double ConvertUnsigned(uint value) => value;

    static double ConvertUnsignedWide(ulong value) => value;

    static int ConvertInt(double value) => (int)value;

    static int Compare(double a, double b)
    {
        bool less = a < b,
            le = a <= b,
            greater = a > b,
            ge = a >= b,
            eq = a == b,
            ne = a != b;
        int mask =
            (less ? 1 : 0)
            | (le ? 2 : 0)
            | (greater ? 4 : 0)
            | (ge ? 8 : 0)
            | (eq ? 16 : 0)
            | (ne ? 32 : 0);
        if (a < b)
            mask |= 64;
        if (a <= b)
            mask |= 128;
        if (a > b)
            mask |= 256;
        if (a >= b)
            mask |= 512;
        if (a == b)
            mask |= 1024;
        if (a != b)
            mask |= 2048;
        return mask;
    }

    public static int CompareNan() => Store((ulong)(uint)Compare(From(0x7ff0123456789abc), 1.0));

    public static int CompareNegative() => Store((ulong)(uint)Compare(-1.0, -2.0));

    public static int CompareZero() => Store((ulong)(uint)Compare(From(0x8000000000000000), 0.0));

    public static int CompareForward() => Store((ulong)(uint)Compare(1.5, 2.0));

    public static int CompareEqual() => Store((ulong)(uint)Compare(2.0, 2.0));

    public static int CompareFractionEqual() => Store((ulong)(uint)Compare(0.1, 0.1));

    public static int CompareFractions() => Store((ulong)(uint)Compare(0.1, 0.2));

    public static int Multiplication() => Store(Bits(Multiply(2.0, 3.25)));

    public static int MultiplyTiny() => Store(Bits(Multiply(From(1), 1.0)));

    public static int MultiplyHalf() => Store(Bits(Multiply(From(1), 0.5)));

    public static int MultiplyOddHalf() => Store(Bits(Multiply(From(3), 0.5)));

    public static int MultiplyTransition() => Store(Bits(Multiply(From(0x001fffffffffffff), 0.5)));

    public static int MultiplyFraction() => Store(Bits(Multiply(0.1, 0.1)));

    public static int MultiplyCarry() =>
        Store(Bits(Multiply(From(0x3fffffffffffffff), From(0x3ff0000000000001))));

    public static int MultiplyBothCarries() =>
        Store(Bits(Multiply(From(0x3fffffffffffffff), From(0x3fffffffffffffff))));

    public static int MultiplyOverflow() => Store(Bits(Multiply(From(0x7fefffffffffffff), 2.0)));

    public static int MultiplyZero() => Store(Bits(Multiply(From(0x8000000000000000), 2.0)));

    public static int MultiplyInfinity() => Store(Bits(Multiply(-1.0, From(0x7ff0000000000000))));

    public static int MultiplyInvalid() =>
        Store(Bits(Multiply(0.0, From(0x7ff0000000000000))) & 0x7ff8000000000000);

    public static int MultiplyNan() =>
        Store(Bits(Multiply(1.0, From(0x7ff0123456789abc))) & 0x7ff8000000000000);

    public static int OriginalAdd() => Store(Bits(Add(1.25, 3.5)));

    public static int OriginalSubtract() => Store(Bits(Subtract(5.0, 1.5)));

    public static int Division() => Store(Bits(Divide(7.0, 2.0)));

    public static int DivideThird() => Store(Bits(Divide(1.0, 3.0)));

    public static int DivideTiny() => Store(Bits(Divide(From(1), 1.0)));

    public static int DivideHalf() => Store(Bits(Divide(From(1), 2.0)));

    public static int DivideOddHalf() => Store(Bits(Divide(From(3), 2.0)));

    public static int DivideTransition() => Store(Bits(Divide(From(0x001fffffffffffff), 2.0)));

    public static int DivideOverflow() => Store(Bits(Divide(From(0x7fefffffffffffff), 0.5)));

    public static int DivideZero() => Store(Bits(Divide(From(0x8000000000000000), 2.0)));

    public static int DivideInfinity() => Store(Bits(Divide(-1.0, 0.0)));

    public static int DivideInvalid() => Store(Bits(Divide(0.0, 0.0)) & 0x7ff8000000000000);

    public static int DivideUnderflowTie() =>
        Store(Bits(Divide(From(0x0010000000000000), From(0x4340000000000000))));

    public static int DivideUnderflowAbove() =>
        Store(Bits(Divide(From(0x0010000000000001), From(0x4340000000000000))));

    public static int DivideUnderflowBelow() =>
        Store(Bits(Divide(From(0x0010000000000000), From(0x4350000000000000))));

    public static int DivideNegativeHalf() => Store(Bits(Divide(From(0x8000000000000001), 2.0)));

    public static int DivideByInfinity() => Store(Bits(Divide(1.0, From(0xfff0000000000000))));

    public static int DivideInfiniteInvalid() =>
        Store(
            Bits(Divide(From(0x7ff0000000000000), From(0x7ff0000000000000))) & 0x7ff8000000000000
        );

    public static int DivideNan() =>
        Store(Bits(Divide(1.0, From(0x7ff0123456789abc))) & 0x7ff8000000000000);

    public static int IntegerInput() => Store(Bits(Convert(16777217)));

    public static int IntegerUnsigned32() => Store(Bits(ConvertUnsigned(16777217)));

    public static int IntegerUnsigned64() => Store(Bits(ConvertUnsignedWide(0x8000008000000001)));

    public static int RemainderResult() => Store(Bits(Remainder(5.5, 2.0)));

    public static int IntegerOutput() => Store((ulong)ConvertInt(1.5));

    static double Choose(bool first, double a, double b) => first ? a : b;

    static void Step(ref double value, double increment) => value += increment;

    public static int RawTiny() => Store(Koh.GameBoy.SoftFloat.AddF64(1, 1));

    public static int RawWidenTiny() => Store(Koh.GameBoy.SoftFloat.ToF64(1));

    public static int RawMultiplyTiny() =>
        Store(Koh.GameBoy.SoftFloat.MulF64(1, 0x3ff0000000000000));

    public static int RawDivideTiny() => Store(Koh.GameBoy.SoftFloat.DivF64(1, 0x3ff0000000000000));

    public static int Main() => Store(Bits(Add(1.5, 2.5)));

    public static int Opposite() => Store(Bits(Add(-2.5, 0.75)));

    public static int Tiny() => Store(Bits(Add(From(1), From(1))));

    public static int Transition() => Store(Bits(Add(From(0x000fffffffffffff), From(1))));

    public static int Zero() =>
        Store(Bits(Add(From(0x8000000000000000), From(0x8000000000000000))));

    public static int Cancellation() => Store(Bits(Add(1.0, -1.0)));

    public static int Infinity() => Store(Bits(Add(From(0x7ff0000000000000), 1.0)));

    public static int Invalid() =>
        Store(Bits(Add(From(0x7ff0000000000000), From(0xfff0000000000000))) & 0x7ff8000000000000);

    public static int Nan() => Store(Bits(Add(From(0x7ff0123456789abc), 1.0)) & 0x7ff8000000000000);

    public static int Negative() => Store(Bits(Negate(1.5)));

    public static int Difference() => Store(Bits(Subtract(1.5, 2.5)));

    public static int Join() => Store(Bits(Add(Choose(false, 1.5, 2.5), 1.5)));

    public static int Reference()
    {
        double value = 1.5;
        Step(ref value, 2.5);
        return Store(Bits(value));
    }

    public static int Field()
    {
        Snapshot.Value = 1.5;
        Snapshot.Value = Add(Snapshot.Value, 2.5);
        return Store(Bits(Snapshot.Value));
    }

    public static int Array()
    {
        double[] values = new double[2];
        values[0] = 1.5;
        values[1] = 2.5;
        return Store(Bits(Add(values[0], values[1])));
    }

    public static int Widen0() => Store(Bits(Widen(From32(0x3fc00000))));

    public static int Widen1() => Store(Bits(Widen(From32(0xc0200000))));

    public static int Widen2() => Store(Bits(Widen(From32(0x3dcccccd))));

    public static int Widen3() => Store(Bits(Widen(From32(0x00000001))));

    public static int Widen4() => Store(Bits(Widen(From32(0x007fffff))));

    public static int Widen5() => Store(Bits(Widen(From32(0x00800000))));

    public static int Widen6() => Store(Bits(Widen(From32(0x80000000))));

    public static int Widen7() => Store(Bits(Widen(From32(0x7f800000))));

    public static int Widen8() => Store(Bits(Widen(From32(0xff800000))));

    public static int Narrow0() => Store(Bits32(Narrow(From(0x3ff8000000000000))));

    public static int Narrow1() => Store(Bits32(Narrow(From(0xc004000000000000))));

    public static int Narrow2() => Store(Bits32(Narrow(From(0x3fb999999999999a))));

    public static int Narrow3() => Store(Bits32(Narrow(From(0x36a0000000000000))));

    public static int Narrow4() => Store(Bits32(Narrow(From(0x3690000000000000))));

    public static int Narrow5() => Store(Bits32(Narrow(From(0x3690000000000001))));

    public static int Narrow6() => Store(Bits32(Narrow(From(0x36a8000000000000))));

    public static int Narrow7() => Store(Bits32(Narrow(From(0x380fffffe0000000))));

    public static int Narrow8() => Store(Bits32(Narrow(From(0x3ff0000010000000))));

    public static int Narrow9() => Store(Bits32(Narrow(From(0x3ff0000010000001))));

    public static int Narrow10() => Store(Bits32(Narrow(From(0x3ff0000030000000))));

    public static int Narrow11() => Store(Bits32(Narrow(From(0x47effffff0000000))));

    public static int Narrow12() => Store(Bits32(Narrow(From(0x8000000000000000))));

    public static int Narrow13() => Store(Bits32(Narrow(From(0x7ff0000000000000))));

    public static int WidenNan() => Store(Bits(Widen(From32(0x7f812345))) & 0x7ff8000000000000);

    public static int NarrowNan() => Store(Bits32(Narrow(From(0x7ff0123456789abc))) & 0x7fc00000);

    static double InputInt(int value) => value;

    public static int InputInt0() => Store(Bits(InputInt(0)));

    public static int InputInt1() => Store(Bits(InputInt(5)));

    public static int InputInt2() => Store(Bits(InputInt(-5)));

    public static int InputInt3() => Store(Bits(InputInt(1000000)));

    public static int InputInt4() => Store(Bits(InputInt(16777217)));

    public static int InputInt5() => Store(Bits(InputInt(-16777217)));

    public static int InputInt6() => Store(Bits(InputInt(int.MinValue)));

    public static int InputInt7() => Store(Bits(InputInt(2147483647)));

    static double InputUInt(uint value) => value;

    public static int InputUInt0() => Store(Bits(InputUInt(0u)));

    public static int InputUInt1() => Store(Bits(InputUInt(5u)));

    public static int InputUInt2() => Store(Bits(InputUInt(1000000u)));

    public static int InputUInt3() => Store(Bits(InputUInt(16777217u)));

    public static int InputUInt4() => Store(Bits(InputUInt(4294967295u)));

    static double InputLong(long value) => value;

    public static int InputLong0() => Store(Bits(InputLong(long.MinValue)));

    public static int InputLong1() => Store(Bits(InputLong(9223372036854775807L)));

    public static int InputLong2() => Store(Bits(InputLong(9007199254740993L)));

    public static int InputLong3() => Store(Bits(InputLong(9007199254740995L)));

    public static int InputLong4() => Store(Bits(InputLong(-9007199254740995L)));

    static double InputULong(ulong value) => value;

    public static int InputULong0() => Store(Bits(InputULong(0x0000000000000000UL)));

    public static int InputULong1() => Store(Bits(InputULong(0xffffffffffffffffUL)));

    public static int InputULong2() => Store(Bits(InputULong(0x0020000000000001UL)));

    public static int InputULong3() => Store(Bits(InputULong(0x0020000000000003UL)));

    public static int InputULong4() => Store(Bits(InputULong(0x8000000000000400UL)));

    public static int InputULong5() => Store(Bits(InputULong(0x8000000000000401UL)));

    static double InputNative(nint value) => value;

    public static int InputNative0() => Store(Bits(InputNative((nint)(-123))));

    static double InputUNative(nuint value) => value;

    public static int InputUNative0() => Store(Bits(InputUNative((nuint)65535)));

    static int OutputInt(double value) => (int)value;

    public static int OutputInt0() =>
        Store(unchecked((ulong)(long)OutputInt(From(0xc00d99999999999a))));

    public static int OutputInt1() =>
        Store(unchecked((ulong)(long)OutputInt(From(0x400d99999999999a))));

    public static int OutputInt2() =>
        Store(unchecked((ulong)(long)OutputInt(From(0x408f420000000000))));

    public static int OutputInt3() =>
        Store(unchecked((ulong)(long)OutputInt(From(0x41e0000000000000))));

    public static int OutputInt4() =>
        Store(unchecked((ulong)(long)OutputInt(From(0xc1e0000080000000))));

    public static int OutputInt5() =>
        Store(unchecked((ulong)(long)OutputInt(From(0x7ff8000000000000))));

    public static int OutputInt6() =>
        Store(unchecked((ulong)(long)OutputInt(From(0x7ff0000000000000))));

    static uint OutputUInt(double value) => (uint)value;

    public static int OutputUInt0() => Store((ulong)OutputUInt(From(0x400d99999999999a)));

    public static int OutputUInt1() => Store((ulong)OutputUInt(From(0xc00d99999999999a)));

    public static int OutputUInt2() => Store((ulong)OutputUInt(From(0x41f0000000000000)));

    public static int OutputUInt3() => Store((ulong)OutputUInt(From(0x7ff8000000000000)));

    static long OutputLong(double value) => (long)value;

    public static int OutputLong0() =>
        Store(unchecked((ulong)(long)OutputLong(From(0xc00d99999999999a))));

    public static int OutputLong1() =>
        Store(unchecked((ulong)(long)OutputLong(From(0x43e0000000000000))));

    public static int OutputLong2() =>
        Store(unchecked((ulong)(long)OutputLong(From(0xc3e0000000000000))));

    public static int OutputLong3() =>
        Store(unchecked((ulong)(long)OutputLong(From(0x7ff0000000000000))));

    public static int OutputLong4() =>
        Store(unchecked((ulong)(long)OutputLong(From(0xfff0000000000000))));

    public static int OutputLong5() =>
        Store(unchecked((ulong)(long)OutputLong(From(0x7ff8000000000000))));

    static ulong OutputULong(double value) => (ulong)value;

    public static int OutputULong0() => Store((ulong)OutputULong(From(0x43f0000000000000)));

    public static int OutputULong1() => Store((ulong)OutputULong(From(0x43efffffffffffff)));

    public static int OutputULong2() => Store((ulong)OutputULong(From(0xc00d99999999999a)));

    public static int OutputULong3() => Store((ulong)OutputULong(From(0x7ff8000000000000)));

    static sbyte OutputSByte(double value) => (sbyte)value;

    public static int OutputSByte0() =>
        Store(unchecked((ulong)(long)OutputSByte(From(0x4060400000000000))));

    static byte OutputByte(double value) => (byte)value;

    public static int OutputByte0() => Store((ulong)OutputByte(From(0x4072cc0000000000)));

    static short OutputShort(double value) => (short)value;

    public static int OutputShort0() =>
        Store(unchecked((ulong)(long)OutputShort(From(0x40f1170000000000))));

    static ushort OutputUShort(double value) => (ushort)value;

    public static int OutputUShort0() => Store((ulong)OutputUShort(From(0x40f1170000000000)));

    static nint OutputNative(double value) => (nint)value;

    public static int OutputNative0() =>
        Store(unchecked((ulong)(long)OutputNative(From(0x40f1170000000000))));

    static nuint OutputUNative(double value) => (nuint)value;

    public static int OutputUNative0() => Store((ulong)OutputUNative(From(0x40f0000000000000)));

    public static int OutputNativeSignedWrap() =>
        Store(unchecked((ulong)(long)OutputNative(65535.0)));

    public static int OutputShortSignedWrap() =>
        Store(unchecked((ulong)(long)OutputShort(65535.0)));

    public static int RemNegative() =>
        Store(Bits(Remainder(From(0xc016000000000000UL), From(0x4000000000000000UL))));

    public static int RemNegativeDivisor() =>
        Store(Bits(Remainder(From(0x4016000000000000UL), From(0xc000000000000000UL))));

    public static int RemTrunc() =>
        Store(Bits(Remainder(From(0x401c000000000000UL), From(0x4000000000000000UL))));

    public static int RemZero() =>
        Store(Bits(Remainder(From(0x8000000000000000UL), From(0x4000000000000000UL))));

    public static int RemMultiple() =>
        Store(Bits(Remainder(From(0xc010000000000000UL), From(0x4000000000000000UL))));

    public static int RemSmall() =>
        Store(Bits(Remainder(From(0x3ff0000000000000UL), From(0x4000000000000000UL))));

    public static int RemTiny() =>
        Store(Bits(Remainder(From(0x0000000000000003UL), From(0x0000000000000002UL))));

    public static int RemTinyNegative() =>
        Store(Bits(Remainder(From(0x8000000000000003UL), From(0x0000000000000002UL))));

    public static int RemTransition() =>
        Store(Bits(Remainder(From(0x0010000000000000UL), From(0x0000000000000003UL))));

    public static int RemExtreme() =>
        Store(Bits(Remainder(From(0x7fefffffffffffffUL), From(0x0000000000000003UL))));

    public static int RemZeroDivisor() =>
        Store(
            Bits(Remainder(From(0x3ff0000000000000UL), From(0x0000000000000000UL)))
                & 0x7ff8000000000000
        );

    public static int RemInfinity() =>
        Store(
            Bits(Remainder(From(0x7ff0000000000000UL), From(0x4000000000000000UL)))
                & 0x7ff8000000000000
        );

    public static int RemInfInf() =>
        Store(
            Bits(Remainder(From(0x7ff0000000000000UL), From(0x7ff0000000000000UL)))
                & 0x7ff8000000000000
        );

    public static int RemByInfinity() =>
        Store(Bits(Remainder(From(0xbff0000000000000UL), From(0x7ff0000000000000UL))));

    public static int RemNan() =>
        Store(
            Bits(Remainder(From(0x3ff0000000000000UL), From(0x7ff0123456789abcUL)))
                & 0x7ff8000000000000
        );

    public static int RemZeroZero() =>
        Store(
            Bits(Remainder(From(0x0000000000000000UL), From(0x0000000000000000UL)))
                & 0x7ff8000000000000
        );
}
