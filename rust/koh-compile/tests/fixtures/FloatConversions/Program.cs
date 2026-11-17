public static class FloatConversions
{
    static unsafe int Bits(float value) => *(int*)&value;

    static unsafe float FromBits(uint value) => *(float*)&value;

    static float FromInt(int value) => value;

    static float FromUInt(uint value) => value;

    static float FromNative(nint value) => value;

    static float FromUNative(nuint value) => value;

    static int ToInt(float value) => (int)value;

    static uint ToUInt(float value) => (uint)value;

    static int ToSmall(float value) => (sbyte)value;

    static int ToByte(float value) => (byte)value;

    static int ToNative(float value) => (int)(nint)value;

    static float FromLong(long value) => value;

    static float FromULong(ulong value) => value;

    static long ToLong(float value) => (long)value;

    static ulong ToULong(float value) => (ulong)value;

    static unsafe int Store(ulong value)
    {
        *(ulong*)0xd000 = value;
        return 42;
    }

    public static int LongMin() => Bits(FromLong(long.MinValue));

    public static int LongMax() => Bits(FromLong(long.MaxValue));

    public static int ULongMax() => Bits(FromULong(ulong.MaxValue));

    public static int ULongHalfBelow() => Bits(FromULong(0x8000007fffffffff));

    public static int ULongHalfExact() => Bits(FromULong(0x8000008000000000));

    public static int ULongHalfAbove() => Bits(FromULong(0x8000008000000001));

    public static int LongNegative() => Bits(FromLong(-16777217));

    public static int ULongRounded() => Bits(FromULong(16777219));

    public static int LongPositive() => Store(unchecked((ulong)ToLong(3.7f)));

    public static int LongNegativeResult() => Store(unchecked((ulong)ToLong(-3.7f)));

    public static int LongLimit() => Store(unchecked((ulong)ToLong(FromBits(0x5f000000))));

    public static int LongMinResult() => Store(unchecked((ulong)ToLong(FromBits(0xdf000000))));

    public static int LongBelowMin() => Store(unchecked((ulong)ToLong(FromBits(0xdf000001))));

    public static int ULongLimit() => Store(ToULong(FromBits(0x5f800000)));

    public static int ULongLargest() => Store(ToULong(FromBits(0x5f7fffff)));

    public static int LongLargest() => Store(unchecked((ulong)ToLong(FromBits(0x5effffff))));

    public static int ULongNegative() => Store(ToULong(-3.7f));

    public static int LongNan() => Store(unchecked((ulong)ToLong(FromBits(0x7fc12345))));

    public static int LongNegativeNan() => Store(unchecked((ulong)ToLong(FromBits(0xffc12345))));

    public static int LongInfinity() => Store(unchecked((ulong)ToLong(FromBits(0x7f800000))));

    public static int LongNegativeInfinity() =>
        Store(unchecked((ulong)ToLong(FromBits(0xff800000))));

    public static int LongSubnormal() => Store(unchecked((ulong)ToLong(FromBits(1))));

    public static int ULongInfinity() => Store(ToULong(FromBits(0x7f800000)));

    public static int ULongNegativeInfinity() => Store(ToULong(FromBits(0xff800000)));

    static int Checked(float value) => checked((int)value);

    public static int Main() => Bits(FromInt(16777217));

    public static int FromZero() => Bits(FromInt(0));

    public static int FromFive() => Bits(FromInt(5));

    public static int FromNegative() => Bits(FromInt(-5));

    public static int FromMillion() => Bits(FromInt(1000000));

    public static int FromMin() => Bits(FromInt(int.MinValue));

    public static int FromMax() => Bits(FromInt(int.MaxValue));

    public static int UnsignedMax() => Bits(FromUInt(uint.MaxValue));

    public static int NativeNegative() => Bits(FromNative(-2));

    public static int NativeUnsigned() => Bits(FromUNative(65535));

    public static int Positive() => ToInt(3.7f);

    public static int Negative() => ToInt(-3.7f);

    public static int Thousand() => ToInt(1000.25f);

    public static int Nan() => ToInt(FromBits(0x7fc12345));

    public static int NegativeNan() => ToInt(FromBits(0xffc12345));

    public static int Infinity() => ToInt(FromBits(0x7f800000));

    public static int NegativeInfinity() => ToInt(FromBits(0xff800000));

    public static int SignedLimit() => ToInt(FromBits(0x4f000000));

    public static int UnsignedLimit() => unchecked((int)ToUInt(FromBits(0x4f800000)));

    public static int UnsignedLargest() => unchecked((int)ToUInt(FromBits(0x4f7fffff)));

    public static int UnsignedNegative() => unchecked((int)ToUInt(-3.5f));

    public static int SmallWrap() => ToSmall(130.0f);

    public static int ByteWrap() => ToByte(300.75f);

    public static int NativeWrap() => ToNative(32768.0f);

    public static int WideInput() => Bits(FromLong(16777217));

    public static int WideOutput() => unchecked((int)ToLong(1.0f));

    public static int CheckedUnsupported() => Checked(3.7f);
}
