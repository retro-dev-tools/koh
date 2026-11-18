public static class FloatComparisons
{
    static unsafe float From(uint value) => *(float*)&value;

    static unsafe int Bits(float value) => *(int*)&value;

    static bool Less(float a, float b) => a < b;

    static bool LessEqual(float a, float b) => a <= b;

    static bool Greater(float a, float b) => a > b;

    static bool GreaterEqual(float a, float b) => a >= b;

    static bool Equal(float a, float b) => a == b;

    static bool NotEqual(float a, float b) => a != b;

    static int Values(float a, float b) =>
        (Less(a, b) ? 1 : 0)
        | (LessEqual(a, b) ? 2 : 0)
        | (Greater(a, b) ? 4 : 0)
        | (GreaterEqual(a, b) ? 8 : 0)
        | (Equal(a, b) ? 16 : 0)
        | (NotEqual(a, b) ? 32 : 0);

    static int Branches(float a, float b)
    {
        int mask = 0;
        if (a < b)
            mask |= 1;
        if (a <= b)
            mask |= 2;
        if (a > b)
            mask |= 4;
        if (a >= b)
            mask |= 8;
        if (a == b)
            mask |= 16;
        if (a != b)
            mask |= 32;
        return mask;
    }

    static int Evaluate(float a, float b) => Values(a, b) | (Branches(a, b) << 6);

    static float Minimum(float a, float b) => (a < b ? a : b) + 1.0f;

    static int Probe(float a, float b) => a < b ? 12345678 : 87654321;

    public static int ProbeLeftNan() => Probe(From(0x7fc12345u), 1.0f);

    public static int ProbeRightNan() => Probe(1.0f, From(0xffa12345u));

    public static int ProbeLess() => Probe(-2.0f, -1.0f);

    public static int ProbeGreater() => Probe(-1.0f, -2.0f);

    public static int ProbeEqual() => Probe(From(0x80000000u), 0.0f);

    public static int MinForward() => Bits(Minimum(1.5f, 2.5f));

    public static int MinReverse() => Bits(Minimum(2.5f, 1.5f));

    public static int MinNegative() => Bits(Minimum(-1.0f, -2.0f));

    public static int Case0() => Evaluate(From(0x3fc00000u), From(0x40000000u));

    public static int Case1() => Evaluate(From(0x40000000u), From(0x3fc00000u));

    public static int Case2() => Evaluate(From(0x40000000u), From(0x40000000u));

    public static int Case3() => Evaluate(From(0xbf800000u), From(0xc0000000u));

    public static int Case4() => Evaluate(From(0x3f800000u), From(0x3f800000u));

    public static int Case5() => Evaluate(From(0x3dcccccdu), From(0x3dcccccdu));

    public static int Case6() => Evaluate(From(0x3dcccccdu), From(0x3e4ccccdu));

    public static int Case7() => Evaluate(From(0x80000000u), From(0x00000000u));

    public static int Case8() => Evaluate(From(0xc0000000u), From(0xbf800000u));

    public static int Case9() => Evaluate(From(0xbf800000u), From(0xbf800000u));

    public static int Case10() => Evaluate(From(0xbf000000u), From(0xbf000000u));

    public static int Case11() => Evaluate(From(0x7fc12345u), From(0x3f800000u));

    public static int Case12() => Evaluate(From(0x3f800000u), From(0xffc12345u));

    public static int Case13() => Evaluate(From(0x7fa12345u), From(0xffc12345u));

    public static int Case14() => Evaluate(From(0x7f800000u), From(0xff800000u));

    public static int Case15() => Evaluate(From(0xff800000u), From(0xff800000u));

    public static int Case16() => Evaluate(From(0x00000001u), From(0x80000001u));

    public static int Case17() => Evaluate(From(0x00000000u), From(0x80000000u));
}
