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

    public static int Unsupported() => Bits(1.5f / Choose(false, 2.5f, 3.5f));

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
}
