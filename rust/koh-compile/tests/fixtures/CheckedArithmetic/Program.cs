public static class Program
{
    static byte AddByte(byte left, byte right) => checked((byte)(left + right));

    static sbyte SubByte(sbyte left, sbyte right) => checked((sbyte)(left - right));

    static ushort AddShort(ushort left, ushort right) => checked((ushort)(left + right));

    static short SubShort(short left, short right) => checked((short)(left - right));

    public static int Byte() => Store(AddByte(250, 5));

    public static int ByteOverflow() => Store(AddByte(250, 6));

    public static int SignedByte() => Store(unchecked((ulong)(long)SubByte(-128, 0)));

    public static int SignedByteOverflow() => Store(unchecked((ulong)(long)SubByte(-128, 1)));

    public static int UShort() => Store(AddShort(65530, 5));

    public static int UShortOverflow() => Store(AddShort(65530, 6));

    public static int SignedShort() => Store(unchecked((ulong)(long)SubShort(-32768, 0)));

    public static int SignedShortOverflow() => Store(unchecked((ulong)(long)SubShort(-32768, 1)));

    static unsafe int Store(ulong value)
    {
        *(ulong*)0xdff8 = value;
        return 42;
    }

    static int I32Add(int left, int right) => checked(left + right);

    public static int I32Add0() => Store(unchecked((ulong)(long)I32Add(0, 0)));

    public static int I32Add1() => Store(unchecked((ulong)(long)I32Add(1, 2)));

    public static int I32Add2() => Store(unchecked((ulong)(long)I32Add(2147483647, 0)));

    public static int I32Add3() => Store(unchecked((ulong)(long)I32Add(-2147483648, 0)));

    public static int I32Add4() => Store(unchecked((ulong)(long)I32Add(2147483647, 1)));

    public static int I32Add5() => Store(unchecked((ulong)(long)I32Add(-2147483648, -1)));

    public static int I32Add6() => Store(unchecked((ulong)(long)I32Add(2147483647, -1)));

    public static int I32Add7() => Store(unchecked((ulong)(long)I32Add(-2147483648, 1)));

    public static int I32Add8() => Store(unchecked((ulong)(long)I32Add(-1, -1)));

    public static int I32Add9() => Store(unchecked((ulong)(long)I32Add(0, -2147483648)));

    public static int I32Add10() => Store(unchecked((ulong)(long)I32Add(0, 2147483647)));

    public static int I32Add11() => Store(unchecked((ulong)(long)I32Add(2147483647, 2147483647)));

    public static int I32Add12() => Store(unchecked((ulong)(long)I32Add(-2147483648, -2147483648)));

    public static int I32Add13() => Store(unchecked((ulong)(long)I32Add(-2147483648, 2147483647)));

    public static int I32Add14() => Store(unchecked((ulong)(long)I32Add(2147483647, -2147483648)));

    static int I32Sub(int left, int right) => checked(left - right);

    public static int I32Sub0() => Store(unchecked((ulong)(long)I32Sub(0, 0)));

    public static int I32Sub1() => Store(unchecked((ulong)(long)I32Sub(1, 2)));

    public static int I32Sub2() => Store(unchecked((ulong)(long)I32Sub(2147483647, 0)));

    public static int I32Sub3() => Store(unchecked((ulong)(long)I32Sub(-2147483648, 0)));

    public static int I32Sub4() => Store(unchecked((ulong)(long)I32Sub(2147483647, 1)));

    public static int I32Sub5() => Store(unchecked((ulong)(long)I32Sub(-2147483648, -1)));

    public static int I32Sub6() => Store(unchecked((ulong)(long)I32Sub(2147483647, -1)));

    public static int I32Sub7() => Store(unchecked((ulong)(long)I32Sub(-2147483648, 1)));

    public static int I32Sub8() => Store(unchecked((ulong)(long)I32Sub(-1, -1)));

    public static int I32Sub9() => Store(unchecked((ulong)(long)I32Sub(0, -2147483648)));

    public static int I32Sub10() => Store(unchecked((ulong)(long)I32Sub(0, 2147483647)));

    public static int I32Sub11() => Store(unchecked((ulong)(long)I32Sub(2147483647, 2147483647)));

    public static int I32Sub12() => Store(unchecked((ulong)(long)I32Sub(-2147483648, -2147483648)));

    public static int I32Sub13() => Store(unchecked((ulong)(long)I32Sub(-2147483648, 2147483647)));

    public static int I32Sub14() => Store(unchecked((ulong)(long)I32Sub(2147483647, -2147483648)));

    static uint U32Add(uint left, uint right) => checked(left + right);

    public static int U32Add0() => Store(unchecked((ulong)U32Add(0U, 0U)));

    public static int U32Add1() => Store(unchecked((ulong)U32Add(1U, 2U)));

    public static int U32Add2() => Store(unchecked((ulong)U32Add(4294967295U, 0U)));

    public static int U32Add3() => Store(unchecked((ulong)U32Add(0U, 4294967295U)));

    public static int U32Add4() => Store(unchecked((ulong)U32Add(4294967295U, 1U)));

    public static int U32Add5() => Store(unchecked((ulong)U32Add(4294967295U, 4294967295U)));

    public static int U32Add6() => Store(unchecked((ulong)U32Add(4294967294U, 1U)));

    public static int U32Add7() => Store(unchecked((ulong)U32Add(4294967294U, 2U)));

    public static int U32Add8() => Store(unchecked((ulong)U32Add(0U, 1U)));

    public static int U32Add9() => Store(unchecked((ulong)U32Add(1U, 0U)));

    static uint U32Sub(uint left, uint right) => checked(left - right);

    public static int U32Sub0() => Store(unchecked((ulong)U32Sub(0U, 0U)));

    public static int U32Sub1() => Store(unchecked((ulong)U32Sub(1U, 2U)));

    public static int U32Sub2() => Store(unchecked((ulong)U32Sub(4294967295U, 0U)));

    public static int U32Sub3() => Store(unchecked((ulong)U32Sub(0U, 4294967295U)));

    public static int U32Sub4() => Store(unchecked((ulong)U32Sub(4294967295U, 1U)));

    public static int U32Sub5() => Store(unchecked((ulong)U32Sub(4294967295U, 4294967295U)));

    public static int U32Sub6() => Store(unchecked((ulong)U32Sub(4294967294U, 1U)));

    public static int U32Sub7() => Store(unchecked((ulong)U32Sub(4294967294U, 2U)));

    public static int U32Sub8() => Store(unchecked((ulong)U32Sub(0U, 1U)));

    public static int U32Sub9() => Store(unchecked((ulong)U32Sub(1U, 0U)));

    static long I64Add(long left, long right) => checked(left + right);

    public static int I64Add0() => Store(unchecked((ulong)(long)I64Add(0L, 0L)));

    public static int I64Add1() => Store(unchecked((ulong)(long)I64Add(1L, 2L)));

    public static int I64Add2() => Store(unchecked((ulong)(long)I64Add(9223372036854775807L, 0L)));

    public static int I64Add3() => Store(unchecked((ulong)(long)I64Add(-9223372036854775808L, 0L)));

    public static int I64Add4() => Store(unchecked((ulong)(long)I64Add(9223372036854775807L, 1L)));

    public static int I64Add5() =>
        Store(unchecked((ulong)(long)I64Add(-9223372036854775808L, -1L)));

    public static int I64Add6() => Store(unchecked((ulong)(long)I64Add(9223372036854775807L, -1L)));

    public static int I64Add7() => Store(unchecked((ulong)(long)I64Add(-9223372036854775808L, 1L)));

    public static int I64Add8() => Store(unchecked((ulong)(long)I64Add(-1L, -1L)));

    public static int I64Add9() => Store(unchecked((ulong)(long)I64Add(0L, -9223372036854775808L)));

    public static int I64Add10() => Store(unchecked((ulong)(long)I64Add(0L, 9223372036854775807L)));

    public static int I64Add11() =>
        Store(unchecked((ulong)(long)I64Add(9223372036854775807L, 9223372036854775807L)));

    public static int I64Add12() =>
        Store(unchecked((ulong)(long)I64Add(-9223372036854775808L, -9223372036854775808L)));

    public static int I64Add13() =>
        Store(unchecked((ulong)(long)I64Add(-9223372036854775808L, 9223372036854775807L)));

    public static int I64Add14() =>
        Store(unchecked((ulong)(long)I64Add(9223372036854775807L, -9223372036854775808L)));

    static long I64Sub(long left, long right) => checked(left - right);

    public static int I64Sub0() => Store(unchecked((ulong)(long)I64Sub(0L, 0L)));

    public static int I64Sub1() => Store(unchecked((ulong)(long)I64Sub(1L, 2L)));

    public static int I64Sub2() => Store(unchecked((ulong)(long)I64Sub(9223372036854775807L, 0L)));

    public static int I64Sub3() => Store(unchecked((ulong)(long)I64Sub(-9223372036854775808L, 0L)));

    public static int I64Sub4() => Store(unchecked((ulong)(long)I64Sub(9223372036854775807L, 1L)));

    public static int I64Sub5() =>
        Store(unchecked((ulong)(long)I64Sub(-9223372036854775808L, -1L)));

    public static int I64Sub6() => Store(unchecked((ulong)(long)I64Sub(9223372036854775807L, -1L)));

    public static int I64Sub7() => Store(unchecked((ulong)(long)I64Sub(-9223372036854775808L, 1L)));

    public static int I64Sub8() => Store(unchecked((ulong)(long)I64Sub(-1L, -1L)));

    public static int I64Sub9() => Store(unchecked((ulong)(long)I64Sub(0L, -9223372036854775808L)));

    public static int I64Sub10() => Store(unchecked((ulong)(long)I64Sub(0L, 9223372036854775807L)));

    public static int I64Sub11() =>
        Store(unchecked((ulong)(long)I64Sub(9223372036854775807L, 9223372036854775807L)));

    public static int I64Sub12() =>
        Store(unchecked((ulong)(long)I64Sub(-9223372036854775808L, -9223372036854775808L)));

    public static int I64Sub13() =>
        Store(unchecked((ulong)(long)I64Sub(-9223372036854775808L, 9223372036854775807L)));

    public static int I64Sub14() =>
        Store(unchecked((ulong)(long)I64Sub(9223372036854775807L, -9223372036854775808L)));

    static ulong U64Add(ulong left, ulong right) => checked(left + right);

    public static int U64Add0() => Store(unchecked((ulong)U64Add(0UL, 0UL)));

    public static int U64Add1() => Store(unchecked((ulong)U64Add(1UL, 2UL)));

    public static int U64Add2() => Store(unchecked((ulong)U64Add(18446744073709551615UL, 0UL)));

    public static int U64Add3() => Store(unchecked((ulong)U64Add(0UL, 18446744073709551615UL)));

    public static int U64Add4() => Store(unchecked((ulong)U64Add(18446744073709551615UL, 1UL)));

    public static int U64Add5() =>
        Store(unchecked((ulong)U64Add(18446744073709551615UL, 18446744073709551615UL)));

    public static int U64Add6() => Store(unchecked((ulong)U64Add(18446744073709551614UL, 1UL)));

    public static int U64Add7() => Store(unchecked((ulong)U64Add(18446744073709551614UL, 2UL)));

    public static int U64Add8() => Store(unchecked((ulong)U64Add(0UL, 1UL)));

    public static int U64Add9() => Store(unchecked((ulong)U64Add(1UL, 0UL)));

    static ulong U64Sub(ulong left, ulong right) => checked(left - right);

    public static int U64Sub0() => Store(unchecked((ulong)U64Sub(0UL, 0UL)));

    public static int U64Sub1() => Store(unchecked((ulong)U64Sub(1UL, 2UL)));

    public static int U64Sub2() => Store(unchecked((ulong)U64Sub(18446744073709551615UL, 0UL)));

    public static int U64Sub3() => Store(unchecked((ulong)U64Sub(0UL, 18446744073709551615UL)));

    public static int U64Sub4() => Store(unchecked((ulong)U64Sub(18446744073709551615UL, 1UL)));

    public static int U64Sub5() =>
        Store(unchecked((ulong)U64Sub(18446744073709551615UL, 18446744073709551615UL)));

    public static int U64Sub6() => Store(unchecked((ulong)U64Sub(18446744073709551614UL, 1UL)));

    public static int U64Sub7() => Store(unchecked((ulong)U64Sub(18446744073709551614UL, 2UL)));

    public static int U64Sub8() => Store(unchecked((ulong)U64Sub(0UL, 1UL)));

    public static int U64Sub9() => Store(unchecked((ulong)U64Sub(1UL, 0UL)));

    static nint INAdd(nint left, nint right) => checked(left + right);

    public static int INAdd0() => Store(unchecked((ulong)(long)INAdd((nint)(0), (nint)(0))));

    public static int INAdd1() => Store(unchecked((ulong)(long)INAdd((nint)(1), (nint)(2))));

    public static int INAdd2() => Store(unchecked((ulong)(long)INAdd((nint)(32767), (nint)(0))));

    public static int INAdd3() => Store(unchecked((ulong)(long)INAdd((nint)(-32768), (nint)(0))));

    public static int INAdd4() => Store(unchecked((ulong)(long)INAdd((nint)(32767), (nint)(1))));

    public static int INAdd5() => Store(unchecked((ulong)(long)INAdd((nint)(-32768), (nint)(-1))));

    public static int INAdd6() => Store(unchecked((ulong)(long)INAdd((nint)(32767), (nint)(-1))));

    public static int INAdd7() => Store(unchecked((ulong)(long)INAdd((nint)(-32768), (nint)(1))));

    public static int INAdd8() => Store(unchecked((ulong)(long)INAdd((nint)(-1), (nint)(-1))));

    public static int INAdd9() => Store(unchecked((ulong)(long)INAdd((nint)(0), (nint)(-32768))));

    public static int INAdd10() => Store(unchecked((ulong)(long)INAdd((nint)(0), (nint)(32767))));

    public static int INAdd11() =>
        Store(unchecked((ulong)(long)INAdd((nint)(32767), (nint)(32767))));

    public static int INAdd12() =>
        Store(unchecked((ulong)(long)INAdd((nint)(-32768), (nint)(-32768))));

    public static int INAdd13() =>
        Store(unchecked((ulong)(long)INAdd((nint)(-32768), (nint)(32767))));

    public static int INAdd14() =>
        Store(unchecked((ulong)(long)INAdd((nint)(32767), (nint)(-32768))));

    static nint INSub(nint left, nint right) => checked(left - right);

    public static int INSub0() => Store(unchecked((ulong)(long)INSub((nint)(0), (nint)(0))));

    public static int INSub1() => Store(unchecked((ulong)(long)INSub((nint)(1), (nint)(2))));

    public static int INSub2() => Store(unchecked((ulong)(long)INSub((nint)(32767), (nint)(0))));

    public static int INSub3() => Store(unchecked((ulong)(long)INSub((nint)(-32768), (nint)(0))));

    public static int INSub4() => Store(unchecked((ulong)(long)INSub((nint)(32767), (nint)(1))));

    public static int INSub5() => Store(unchecked((ulong)(long)INSub((nint)(-32768), (nint)(-1))));

    public static int INSub6() => Store(unchecked((ulong)(long)INSub((nint)(32767), (nint)(-1))));

    public static int INSub7() => Store(unchecked((ulong)(long)INSub((nint)(-32768), (nint)(1))));

    public static int INSub8() => Store(unchecked((ulong)(long)INSub((nint)(-1), (nint)(-1))));

    public static int INSub9() => Store(unchecked((ulong)(long)INSub((nint)(0), (nint)(-32768))));

    public static int INSub10() => Store(unchecked((ulong)(long)INSub((nint)(0), (nint)(32767))));

    public static int INSub11() =>
        Store(unchecked((ulong)(long)INSub((nint)(32767), (nint)(32767))));

    public static int INSub12() =>
        Store(unchecked((ulong)(long)INSub((nint)(-32768), (nint)(-32768))));

    public static int INSub13() =>
        Store(unchecked((ulong)(long)INSub((nint)(-32768), (nint)(32767))));

    public static int INSub14() =>
        Store(unchecked((ulong)(long)INSub((nint)(32767), (nint)(-32768))));

    static nuint UNAdd(nuint left, nuint right) => checked(left + right);

    public static int UNAdd0() => Store(unchecked((ulong)UNAdd((nuint)0U, (nuint)0U)));

    public static int UNAdd1() => Store(unchecked((ulong)UNAdd((nuint)1U, (nuint)2U)));

    public static int UNAdd2() => Store(unchecked((ulong)UNAdd((nuint)65535U, (nuint)0U)));

    public static int UNAdd3() => Store(unchecked((ulong)UNAdd((nuint)0U, (nuint)65535U)));

    public static int UNAdd4() => Store(unchecked((ulong)UNAdd((nuint)65535U, (nuint)1U)));

    public static int UNAdd5() => Store(unchecked((ulong)UNAdd((nuint)65535U, (nuint)65535U)));

    public static int UNAdd6() => Store(unchecked((ulong)UNAdd((nuint)65534U, (nuint)1U)));

    public static int UNAdd7() => Store(unchecked((ulong)UNAdd((nuint)65534U, (nuint)2U)));

    public static int UNAdd8() => Store(unchecked((ulong)UNAdd((nuint)0U, (nuint)1U)));

    public static int UNAdd9() => Store(unchecked((ulong)UNAdd((nuint)1U, (nuint)0U)));

    static nuint UNSub(nuint left, nuint right) => checked(left - right);

    public static int UNSub0() => Store(unchecked((ulong)UNSub((nuint)0U, (nuint)0U)));

    public static int UNSub1() => Store(unchecked((ulong)UNSub((nuint)1U, (nuint)2U)));

    public static int UNSub2() => Store(unchecked((ulong)UNSub((nuint)65535U, (nuint)0U)));

    public static int UNSub3() => Store(unchecked((ulong)UNSub((nuint)0U, (nuint)65535U)));

    public static int UNSub4() => Store(unchecked((ulong)UNSub((nuint)65535U, (nuint)1U)));

    public static int UNSub5() => Store(unchecked((ulong)UNSub((nuint)65535U, (nuint)65535U)));

    public static int UNSub6() => Store(unchecked((ulong)UNSub((nuint)65534U, (nuint)1U)));

    public static int UNSub7() => Store(unchecked((ulong)UNSub((nuint)65534U, (nuint)2U)));

    public static int UNSub8() => Store(unchecked((ulong)UNSub((nuint)0U, (nuint)1U)));

    public static int UNSub9() => Store(unchecked((ulong)UNSub((nuint)1U, (nuint)0U)));

    public static int Discard()
    {
        I32Add(int.MaxValue, 1);
        return Store(0);
    }

    public static int Loop()
    {
        int sum = 0;
        for (int i = 0; i < 20; i++)
            sum = checked(sum + i);
        return Store((ulong)sum);
    }

    public static int LoopOverflow()
    {
        int sum = int.MaxValue - 1;
        for (int i = 0; i < 3; i++)
            sum = checked(sum + 1);
        return Store((ulong)sum);
    }

    static int I32Mul(int left, int right) => checked(left * right);

    public static int I32Mul0() => Store(unchecked((ulong)(long)I32Mul(0, 0)));

    public static int I32Mul1() => Store(unchecked((ulong)(long)I32Mul(1, 2)));

    public static int I32Mul2() => Store(unchecked((ulong)(long)I32Mul(2147483647, 0)));

    public static int I32Mul3() => Store(unchecked((ulong)(long)I32Mul(-2147483648, 0)));

    public static int I32Mul4() => Store(unchecked((ulong)(long)I32Mul(2147483647, 1)));

    public static int I32Mul5() => Store(unchecked((ulong)(long)I32Mul(-2147483648, -1)));

    public static int I32Mul6() => Store(unchecked((ulong)(long)I32Mul(2147483647, -1)));

    public static int I32Mul7() => Store(unchecked((ulong)(long)I32Mul(-2147483648, 1)));

    public static int I32Mul8() => Store(unchecked((ulong)(long)I32Mul(-1, -1)));

    public static int I32Mul9() => Store(unchecked((ulong)(long)I32Mul(0, -2147483648)));

    public static int I32Mul10() => Store(unchecked((ulong)(long)I32Mul(0, 2147483647)));

    public static int I32Mul11() => Store(unchecked((ulong)(long)I32Mul(2147483647, 2147483647)));

    public static int I32Mul12() => Store(unchecked((ulong)(long)I32Mul(-2147483648, -2147483648)));

    public static int I32Mul13() => Store(unchecked((ulong)(long)I32Mul(-2147483648, 2147483647)));

    public static int I32Mul14() => Store(unchecked((ulong)(long)I32Mul(2147483647, -2147483648)));

    public static int I32Mul15() => Store(unchecked((ulong)(long)I32Mul(46340, 46340)));

    public static int I32Mul16() => Store(unchecked((ulong)(long)I32Mul(46341, 46341)));

    public static int I32Mul17() => Store(unchecked((ulong)(long)I32Mul(-46340, 46340)));

    public static int I32Mul18() => Store(unchecked((ulong)(long)I32Mul(-46341, 46341)));

    static uint U32Mul(uint left, uint right) => checked(left * right);

    public static int U32Mul0() => Store(unchecked((ulong)U32Mul(0U, 0U)));

    public static int U32Mul1() => Store(unchecked((ulong)U32Mul(1U, 2U)));

    public static int U32Mul2() => Store(unchecked((ulong)U32Mul(4294967295U, 0U)));

    public static int U32Mul3() => Store(unchecked((ulong)U32Mul(0U, 4294967295U)));

    public static int U32Mul4() => Store(unchecked((ulong)U32Mul(4294967295U, 1U)));

    public static int U32Mul5() => Store(unchecked((ulong)U32Mul(4294967295U, 4294967295U)));

    public static int U32Mul6() => Store(unchecked((ulong)U32Mul(4294967294U, 1U)));

    public static int U32Mul7() => Store(unchecked((ulong)U32Mul(4294967294U, 2U)));

    public static int U32Mul8() => Store(unchecked((ulong)U32Mul(0U, 1U)));

    public static int U32Mul9() => Store(unchecked((ulong)U32Mul(1U, 0U)));

    public static int U32Mul10() => Store(unchecked((ulong)U32Mul(65535U, 65535U)));

    public static int U32Mul11() => Store(unchecked((ulong)U32Mul(65536U, 65536U)));

    public static int U32Mul12() => Store(unchecked((ulong)U32Mul(4294967295U, 2U)));

    public static int U32Mul13() => Store(unchecked((ulong)U32Mul(2147483647U, 2U)));

    static long I64Mul(long left, long right) => checked(left * right);

    public static int I64Mul0() => Store(unchecked((ulong)(long)I64Mul(0L, 0L)));

    public static int I64Mul1() => Store(unchecked((ulong)(long)I64Mul(1L, 2L)));

    public static int I64Mul2() => Store(unchecked((ulong)(long)I64Mul(9223372036854775807L, 0L)));

    public static int I64Mul3() => Store(unchecked((ulong)(long)I64Mul(-9223372036854775808L, 0L)));

    public static int I64Mul4() => Store(unchecked((ulong)(long)I64Mul(9223372036854775807L, 1L)));

    public static int I64Mul5() =>
        Store(unchecked((ulong)(long)I64Mul(-9223372036854775808L, -1L)));

    public static int I64Mul6() => Store(unchecked((ulong)(long)I64Mul(9223372036854775807L, -1L)));

    public static int I64Mul7() => Store(unchecked((ulong)(long)I64Mul(-9223372036854775808L, 1L)));

    public static int I64Mul8() => Store(unchecked((ulong)(long)I64Mul(-1L, -1L)));

    public static int I64Mul9() => Store(unchecked((ulong)(long)I64Mul(0L, -9223372036854775808L)));

    public static int I64Mul10() => Store(unchecked((ulong)(long)I64Mul(0L, 9223372036854775807L)));

    public static int I64Mul11() =>
        Store(unchecked((ulong)(long)I64Mul(9223372036854775807L, 9223372036854775807L)));

    public static int I64Mul12() =>
        Store(unchecked((ulong)(long)I64Mul(-9223372036854775808L, -9223372036854775808L)));

    public static int I64Mul13() =>
        Store(unchecked((ulong)(long)I64Mul(-9223372036854775808L, 9223372036854775807L)));

    public static int I64Mul14() =>
        Store(unchecked((ulong)(long)I64Mul(9223372036854775807L, -9223372036854775808L)));

    public static int I64Mul15() => Store(unchecked((ulong)(long)I64Mul(3037000499L, 3037000499L)));

    public static int I64Mul16() => Store(unchecked((ulong)(long)I64Mul(3037000500L, 3037000500L)));

    public static int I64Mul17() =>
        Store(unchecked((ulong)(long)I64Mul(-3037000499L, 3037000499L)));

    public static int I64Mul18() =>
        Store(unchecked((ulong)(long)I64Mul(-3037000500L, 3037000500L)));

    public static int I64Mul19() => Store(unchecked((ulong)(long)I64Mul(4294967296L, 2147483647L)));

    public static int I64Mul20() =>
        Store(unchecked((ulong)(long)I64Mul(-4294967296L, 2147483648L)));

    public static int I64Mul21() =>
        Store(unchecked((ulong)(long)I64Mul(-4294967296L, 2147483649L)));

    static ulong U64Mul(ulong left, ulong right) => checked(left * right);

    public static int U64Mul0() => Store(unchecked((ulong)U64Mul(0UL, 0UL)));

    public static int U64Mul1() => Store(unchecked((ulong)U64Mul(1UL, 2UL)));

    public static int U64Mul2() => Store(unchecked((ulong)U64Mul(18446744073709551615UL, 0UL)));

    public static int U64Mul3() => Store(unchecked((ulong)U64Mul(0UL, 18446744073709551615UL)));

    public static int U64Mul4() => Store(unchecked((ulong)U64Mul(18446744073709551615UL, 1UL)));

    public static int U64Mul5() =>
        Store(unchecked((ulong)U64Mul(18446744073709551615UL, 18446744073709551615UL)));

    public static int U64Mul6() => Store(unchecked((ulong)U64Mul(18446744073709551614UL, 1UL)));

    public static int U64Mul7() => Store(unchecked((ulong)U64Mul(18446744073709551614UL, 2UL)));

    public static int U64Mul8() => Store(unchecked((ulong)U64Mul(0UL, 1UL)));

    public static int U64Mul9() => Store(unchecked((ulong)U64Mul(1UL, 0UL)));

    public static int U64Mul10() => Store(unchecked((ulong)U64Mul(4294967295UL, 4294967295UL)));

    public static int U64Mul11() => Store(unchecked((ulong)U64Mul(4294967296UL, 4294967296UL)));

    public static int U64Mul12() => Store(unchecked((ulong)U64Mul(18446744073709551615UL, 2UL)));

    public static int U64Mul13() => Store(unchecked((ulong)U64Mul(9223372036854775807UL, 2UL)));

    public static int U64Mul14() => Store(unchecked((ulong)U64Mul(4294967296UL, 4294967295UL)));

    public static int U64Mul15() =>
        Store(unchecked((ulong)U64Mul(18446744073709551614UL, 8589934591UL)));

    static nint INMul(nint left, nint right) => checked(left * right);

    public static int INMul0() => Store(unchecked((ulong)(long)INMul((nint)(0), (nint)(0))));

    public static int INMul1() => Store(unchecked((ulong)(long)INMul((nint)(1), (nint)(2))));

    public static int INMul2() => Store(unchecked((ulong)(long)INMul((nint)(32767), (nint)(0))));

    public static int INMul3() => Store(unchecked((ulong)(long)INMul((nint)(-32768), (nint)(0))));

    public static int INMul4() => Store(unchecked((ulong)(long)INMul((nint)(32767), (nint)(1))));

    public static int INMul5() => Store(unchecked((ulong)(long)INMul((nint)(-32768), (nint)(-1))));

    public static int INMul6() => Store(unchecked((ulong)(long)INMul((nint)(32767), (nint)(-1))));

    public static int INMul7() => Store(unchecked((ulong)(long)INMul((nint)(-32768), (nint)(1))));

    public static int INMul8() => Store(unchecked((ulong)(long)INMul((nint)(-1), (nint)(-1))));

    public static int INMul9() => Store(unchecked((ulong)(long)INMul((nint)(0), (nint)(-32768))));

    public static int INMul10() => Store(unchecked((ulong)(long)INMul((nint)(0), (nint)(32767))));

    public static int INMul11() =>
        Store(unchecked((ulong)(long)INMul((nint)(32767), (nint)(32767))));

    public static int INMul12() =>
        Store(unchecked((ulong)(long)INMul((nint)(-32768), (nint)(-32768))));

    public static int INMul13() =>
        Store(unchecked((ulong)(long)INMul((nint)(-32768), (nint)(32767))));

    public static int INMul14() =>
        Store(unchecked((ulong)(long)INMul((nint)(32767), (nint)(-32768))));

    public static int INMul15() => Store(unchecked((ulong)(long)INMul((nint)(181), (nint)(181))));

    public static int INMul16() => Store(unchecked((ulong)(long)INMul((nint)(182), (nint)(182))));

    public static int INMul17() => Store(unchecked((ulong)(long)INMul((nint)(-181), (nint)(181))));

    public static int INMul18() => Store(unchecked((ulong)(long)INMul((nint)(-182), (nint)(182))));

    static nuint UNMul(nuint left, nuint right) => checked(left * right);

    public static int UNMul0() => Store(unchecked((ulong)UNMul((nuint)0U, (nuint)0U)));

    public static int UNMul1() => Store(unchecked((ulong)UNMul((nuint)1U, (nuint)2U)));

    public static int UNMul2() => Store(unchecked((ulong)UNMul((nuint)65535U, (nuint)0U)));

    public static int UNMul3() => Store(unchecked((ulong)UNMul((nuint)0U, (nuint)65535U)));

    public static int UNMul4() => Store(unchecked((ulong)UNMul((nuint)65535U, (nuint)1U)));

    public static int UNMul5() => Store(unchecked((ulong)UNMul((nuint)65535U, (nuint)65535U)));

    public static int UNMul6() => Store(unchecked((ulong)UNMul((nuint)65534U, (nuint)1U)));

    public static int UNMul7() => Store(unchecked((ulong)UNMul((nuint)65534U, (nuint)2U)));

    public static int UNMul8() => Store(unchecked((ulong)UNMul((nuint)0U, (nuint)1U)));

    public static int UNMul9() => Store(unchecked((ulong)UNMul((nuint)1U, (nuint)0U)));

    public static int UNMul10() => Store(unchecked((ulong)UNMul((nuint)255U, (nuint)255U)));

    public static int UNMul11() => Store(unchecked((ulong)UNMul((nuint)256U, (nuint)256U)));

    public static int UNMul12() => Store(unchecked((ulong)UNMul((nuint)65535U, (nuint)2U)));

    public static int UNMul13() => Store(unchecked((ulong)UNMul((nuint)32767U, (nuint)2U)));

    static byte MultiplyByte(byte a, byte b) => checked((byte)(a * b));

    static sbyte MultiplySignedByte(sbyte a, sbyte b) => checked((sbyte)(a * b));

    static ushort MultiplyShort(ushort a, ushort b) => checked((ushort)(a * b));

    static short MultiplySignedShort(short a, short b) => checked((short)(a * b));

    public static int MulByte() => Store(MultiplyByte(15, 17));

    public static int MulByteOverflow() => Store(MultiplyByte(16, 16));

    public static int MulSignedByte() => Store(unchecked((ulong)(long)MultiplySignedByte(-128, 1)));

    public static int MulSignedByteOverflow() =>
        Store(unchecked((ulong)(long)MultiplySignedByte(-128, -1)));

    public static int MulShort() => Store(MultiplyShort(255, 257));

    public static int MulShortOverflow() => Store(MultiplyShort(256, 256));

    public static int MulSignedShort() =>
        Store(unchecked((ulong)(long)MultiplySignedShort(-16384, 2)));

    public static int MulSignedShortOverflow() =>
        Store(unchecked((ulong)(long)MultiplySignedShort(-16385, 2)));

    public static int MulDiscard()
    {
        U64Mul(ulong.MaxValue, 2);
        return Store(0);
    }

    public static int MulLoop()
    {
        uint n = 1;
        for (int i = 0; i < 8; i++)
            n = checked(n * 2);
        return Store(n);
    }

    public static int MulLoopOverflow()
    {
        uint n = 0x80000000;
        for (int i = 0; i < 2; i++)
            n = checked(n * 2);
        return Store(n);
    }
}
