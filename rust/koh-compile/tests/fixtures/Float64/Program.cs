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

    static double Add(double a, double b) => a + b;

    static double Subtract(double a, double b) => a - b;

    static double Negate(double value) => -value;

    static double Multiply(double a, double b) => a * b;

    static double Divide(double a, double b) => a / b;

    static double Convert(long value) => value;

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

    public static int UnsupportedMultiply() => Store(Bits(Multiply(1.5, 2.5)));

    public static int UnsupportedDivide() => Store(Bits(Divide(1.5, 2.5)));

    public static int UnsupportedConversion() => Store(Bits(Convert(16777217)));

    public static int UnsupportedInteger() => ConvertInt(1.5);

    static double Choose(bool first, double a, double b) => first ? a : b;

    static void Step(ref double value, double increment) => value += increment;

    public static int RawTiny() => Store(Koh.GameBoy.SoftFloat.AddF64(1, 1));

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
}
