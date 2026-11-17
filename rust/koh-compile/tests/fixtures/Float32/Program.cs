public static class Program
{
    struct Cell
    {
        public float Value;
    }

    static Cell Snapshot;

    static float Add(float left, float right) => left + right;

    static unsafe int Bits(float value) => *(int*)&value;

    static unsafe float FromBits(int value) => *(float*)&value;

    static float Subtract(float left, float right) => left - right;

    static float Negate(float value) => -value;

    static float Multiply(float left, float right) => left * right;

    static float Divide(float left, float right) => left / right;

    static float Remainder(float left, float right) => left % right;

    public static int RawQuotient() => unchecked((int)Koh.GameBoy.SoftFloat.DivF32(1, 0x3f800000));

    public static int Quotient() => Bits(Divide(7.0f, 2.0f));

    public static int RepeatingQuotient() => Bits(Divide(1.0f, 3.0f));

    public static int NegativeQuotient() => Bits(Divide(-9.0f, 4.0f));

    public static int TinyQuotient() => Bits(Divide(FromBits(3), 2.0f));

    public static int SubnormalQuotient() => Bits(Divide(FromBits(0x00800000), 2.0f));

    public static int TransitionQuotient() => Bits(Divide(FromBits(0x00ffffff), 2.0f));

    public static int DeepQuotient() => Bits(Divide(FromBits(1), FromBits(0x7f7fffff)));

    public static int OverflowQuotient() => Bits(Divide(FromBits(0x7f7fffff), FromBits(1)));

    public static int ZeroQuotient() => Bits(Divide(FromBits(unchecked((int)0x80000000)), 1.0f));

    public static int ZeroDivisor() => Bits(Divide(1.0f, FromBits(0)));

    public static int InvalidQuotient() => Bits(Divide(FromBits(0), FromBits(0))) & 0x7fc00000;

    public static int InfiniteQuotient() =>
        Bits(Divide(FromBits(0x7f800000), FromBits(0x7f800000))) & 0x7fc00000;

    public static int FiniteOverInfinity() => Bits(Divide(-1.0f, FromBits(0x7f800000)));

    public static int InfinityOverFinite() => Bits(Divide(FromBits(0x7f800000), -1.0f));

    public static int NanQuotient() => Bits(Divide(1.0f, FromBits(0x7fa12345))) & 0x7fc00000;

    public static int RoundedSum() => Bits(Add(0.1f, 0.2f));

    public static int PositiveDifference() => Bits(Subtract(3.5f, 1.25f));

    public static int OriginalDifference() => Bits(Subtract(1.0f, 4.0f));

    public static int RawProduct() => unchecked((int)Koh.GameBoy.SoftFloat.MulF32(1, 0x3f800000));

    public static int Product() => Bits(Multiply(2.0f, 3.0f));

    public static int TransitionProduct() =>
        Bits(Multiply(FromBits(0x007fffff), FromBits(0x3f800001)));

    public static int HalfProduct() => Bits(Multiply(FromBits(1), 0.5f));

    public static int DeepUnderflowProduct() => Bits(Multiply(FromBits(1), FromBits(1)));

    public static int ZeroProduct() => Bits(Multiply(FromBits(unchecked((int)0x80000000)), 2.0f));

    public static int OverflowProduct() =>
        Bits(Multiply(FromBits(0x7f7ffffe), FromBits(0x3f800001)));

    public static int InfinityProduct() => Bits(Multiply(FromBits(0x7f800000), -0.5f));

    public static int NanProduct() => Bits(Multiply(FromBits(0x7fa12345), 1.0f)) & 0x7fc00000;

    public static int NegativeProduct() => Bits(Multiply(-1.5f, 4.0f));

    public static int TinyProduct() => Bits(Multiply(FromBits(1), 1.5f));

    public static int UnderflowProduct() => Bits(Multiply(FromBits(0x00800000), 0.5f));

    public static int InvalidProduct() => Bits(Multiply(0.0f, FromBits(0x7f800000))) & 0x7fc00000;

    public static int TieProduct() => Bits(Multiply(FromBits(0x3f800800), FromBits(0x3f801800)));

    public static int Difference() => Bits(Subtract(1.5f, 2.5f));

    public static int TinyDifference() => Bits(Subtract(FromBits(1), FromBits(2)));

    public static int InvalidDifference() =>
        Bits(Subtract(FromBits(0x7f800000), FromBits(0x7f800000))) & 0x7fc00000;

    public static int Negated() => Bits(Negate(1.5f));

    public static int NegatedNegative() => Bits(Negate(-2.5f));

    public static int NegatedZero() => Bits(Negate(FromBits(0)));

    public static int NegatedTiny() => Bits(Negate(FromBits(1)));

    public static int NegatedInfinity() => Bits(Negate(FromBits(0x7f800000)));

    public static int NegatedNan() => Bits(Negate(FromBits(0x7fc12345)));

    static float Choose(bool first, float left, float right) => first ? left : right;

    static void Step(ref float value, float increment) => value = value + increment;

    public static int Main() => Bits(Add(1.5f, 2.5f));

    public static int Negative() => Bits(Add(-2.5f, 0.75f));

    public static int Join() => Bits(Add(Choose(false, 1.5f, 2.5f), 1.5f));

    public static int Reference()
    {
        float value = 1.5f;
        Step(ref value, 2.5f);
        return Bits(value);
    }

    public static int RemainderResult() => Bits(Remainder(5.5f, 2.0f));

    public static int Field()
    {
        Snapshot.Value = 1.5f;
        Snapshot.Value = Add(Snapshot.Value, 2.5f);
        return Bits(Snapshot.Value);
    }

    public static int Array()
    {
        float[] values = new float[2];
        values[0] = 1.5f;
        values[1] = 2.5f;
        return Bits(Add(values[0], values[1]));
    }

    public static int RemNegative() =>
        Bits(
            Remainder(FromBits(unchecked((int)0xc0b00000u)), FromBits(unchecked((int)0x40000000u)))
        );

    public static int RemNegativeDivisor() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x40b00000u)), FromBits(unchecked((int)0xc0000000u)))
        );

    public static int RemTrunc() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x40e00000u)), FromBits(unchecked((int)0x40000000u)))
        );

    public static int RemZero() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x80000000u)), FromBits(unchecked((int)0x40000000u)))
        );

    public static int RemMultiple() =>
        Bits(
            Remainder(FromBits(unchecked((int)0xc0800000u)), FromBits(unchecked((int)0x40000000u)))
        );

    public static int RemSmall() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x3f800000u)), FromBits(unchecked((int)0x40000000u)))
        );

    public static int RemTiny() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x00000003u)), FromBits(unchecked((int)0x00000002u)))
        );

    public static int RemTinyNegative() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x80000003u)), FromBits(unchecked((int)0x00000002u)))
        );

    public static int RemTransition() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x00800000u)), FromBits(unchecked((int)0x00000003u)))
        );

    public static int RemExtreme() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x7f7ffffeu)), FromBits(unchecked((int)0x00000003u)))
        );

    public static int RemZeroDivisor() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x3f800000u)), FromBits(unchecked((int)0x00000000u)))
        ) & 0x7fc00000;

    public static int RemInfinity() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x7f800000u)), FromBits(unchecked((int)0x40000000u)))
        ) & 0x7fc00000;

    public static int RemInfInf() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x7f800000u)), FromBits(unchecked((int)0x7f800000u)))
        ) & 0x7fc00000;

    public static int RemByInfinity() =>
        Bits(
            Remainder(FromBits(unchecked((int)0xbf800000u)), FromBits(unchecked((int)0x7f800000u)))
        );

    public static int RemNan() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x3f800000u)), FromBits(unchecked((int)0x7fa12345u)))
        ) & 0x7fc00000;

    public static int RemZeroZero() =>
        Bits(
            Remainder(FromBits(unchecked((int)0x00000000u)), FromBits(unchecked((int)0x00000000u)))
        ) & 0x7fc00000;
}
