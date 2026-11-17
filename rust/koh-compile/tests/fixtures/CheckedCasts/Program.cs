public static class CheckedCasts
{
    static unsafe int Store(ulong bits)
    {
        *(ulong*)0xdff8 = bits;
        return 42;
    }

    static unsafe double From64(ulong bits) => *(double*)&bits;

    static unsafe float From32(uint bits) => *(float*)&bits;

    static sbyte Cast0long(long value) => checked((sbyte)value);

    public static int I0long0() => Store(unchecked((ulong)(long)Cast0long(-128L)));

    public static int I0long1() => Store(unchecked((ulong)(long)Cast0long(127L)));

    public static int I0long2() => Store(unchecked((ulong)(long)Cast0long(-129L)));

    public static int I0long3() => Store(unchecked((ulong)(long)Cast0long(128L)));

    static sbyte Cast0ulong(ulong value) => checked((sbyte)value);

    public static int I0ulong0() => Store(unchecked((ulong)(long)Cast0ulong(0x0000000000000000UL)));

    public static int I0ulong1() => Store(unchecked((ulong)(long)Cast0ulong(0x000000000000007fUL)));

    public static int I0ulong2() => Store(unchecked((ulong)(long)Cast0ulong(0x0000000000000080UL)));

    static sbyte Cast0float(float value) => checked((sbyte)value);

    public static int F0float0() => Store(unchecked((ulong)(long)Cast0float(From32(0x406ccccdu))));

    public static int F0float1() => Store(unchecked((ulong)(long)Cast0float(From32(0xbf666666u))));

    public static int F0float2() => Store(unchecked((ulong)(long)Cast0float(From32(0xc3000000u))));

    public static int F0float3() => Store(unchecked((ulong)(long)Cast0float(From32(0x43000000u))));

    public static int F0float4() => Store(unchecked((ulong)(long)Cast0float(From32(0xc300c000u))));

    public static int F0float5() => Store(unchecked((ulong)(long)Cast0float(From32(0xc3010000u))));

    public static int F0float6() => Store(unchecked((ulong)(long)Cast0float(From32(0x42ff8000u))));

    static sbyte Cast0double(double value) => checked((sbyte)value);

    public static int F0double0() =>
        Store(unchecked((ulong)(long)Cast0double(From64(0x400d99999999999aUL))));

    public static int F0double1() =>
        Store(unchecked((ulong)(long)Cast0double(From64(0xbfeccccccccccccdUL))));

    public static int F0double2() =>
        Store(unchecked((ulong)(long)Cast0double(From64(0xc060000000000000UL))));

    public static int F0double3() =>
        Store(unchecked((ulong)(long)Cast0double(From64(0x4060000000000000UL))));

    public static int F0double4() =>
        Store(unchecked((ulong)(long)Cast0double(From64(0xc060180000000000UL))));

    public static int F0double5() =>
        Store(unchecked((ulong)(long)Cast0double(From64(0xc060200000000000UL))));

    public static int F0double6() =>
        Store(unchecked((ulong)(long)Cast0double(From64(0x405ff00000000000UL))));

    static byte Cast1long(long value) => checked((byte)value);

    public static int I1long0() => Store((ulong)Cast1long(0L));

    public static int I1long1() => Store((ulong)Cast1long(255L));

    public static int I1long2() => Store((ulong)Cast1long(-1L));

    public static int I1long3() => Store((ulong)Cast1long(256L));

    static byte Cast1ulong(ulong value) => checked((byte)value);

    public static int I1ulong0() => Store((ulong)Cast1ulong(0x0000000000000000UL));

    public static int I1ulong1() => Store((ulong)Cast1ulong(0x00000000000000ffUL));

    public static int I1ulong2() => Store((ulong)Cast1ulong(0x0000000000000100UL));

    static byte Cast1float(float value) => checked((byte)value);

    public static int F1float0() => Store((ulong)Cast1float(From32(0x406ccccdu)));

    public static int F1float1() => Store((ulong)Cast1float(From32(0xbf666666u)));

    public static int F1float2() => Store((ulong)Cast1float(From32(0x00000000u)));

    public static int F1float3() => Store((ulong)Cast1float(From32(0x43800000u)));

    public static int F1float4() => Store((ulong)Cast1float(From32(0xbf400000u)));

    public static int F1float5() => Store((ulong)Cast1float(From32(0xbf800000u)));

    public static int F1float6() => Store((ulong)Cast1float(From32(0x437fc000u)));

    static byte Cast1double(double value) => checked((byte)value);

    public static int F1double0() => Store((ulong)Cast1double(From64(0x400d99999999999aUL)));

    public static int F1double1() => Store((ulong)Cast1double(From64(0xbfeccccccccccccdUL)));

    public static int F1double2() => Store((ulong)Cast1double(From64(0x0000000000000000UL)));

    public static int F1double3() => Store((ulong)Cast1double(From64(0x4070000000000000UL)));

    public static int F1double4() => Store((ulong)Cast1double(From64(0xbfe8000000000000UL)));

    public static int F1double5() => Store((ulong)Cast1double(From64(0xbff0000000000000UL)));

    public static int F1double6() => Store((ulong)Cast1double(From64(0x406ff80000000000UL)));

    static short Cast2long(long value) => checked((short)value);

    public static int I2long0() => Store(unchecked((ulong)(long)Cast2long(-32768L)));

    public static int I2long1() => Store(unchecked((ulong)(long)Cast2long(32767L)));

    public static int I2long2() => Store(unchecked((ulong)(long)Cast2long(-32769L)));

    public static int I2long3() => Store(unchecked((ulong)(long)Cast2long(32768L)));

    static short Cast2ulong(ulong value) => checked((short)value);

    public static int I2ulong0() => Store(unchecked((ulong)(long)Cast2ulong(0x0000000000000000UL)));

    public static int I2ulong1() => Store(unchecked((ulong)(long)Cast2ulong(0x0000000000007fffUL)));

    public static int I2ulong2() => Store(unchecked((ulong)(long)Cast2ulong(0x0000000000008000UL)));

    static short Cast2float(float value) => checked((short)value);

    public static int F2float0() => Store(unchecked((ulong)(long)Cast2float(From32(0x406ccccdu))));

    public static int F2float1() => Store(unchecked((ulong)(long)Cast2float(From32(0xbf666666u))));

    public static int F2float2() => Store(unchecked((ulong)(long)Cast2float(From32(0xc7000000u))));

    public static int F2float3() => Store(unchecked((ulong)(long)Cast2float(From32(0x47000000u))));

    public static int F2float4() => Store(unchecked((ulong)(long)Cast2float(From32(0xc70000c0u))));

    public static int F2float5() => Store(unchecked((ulong)(long)Cast2float(From32(0xc7000100u))));

    public static int F2float6() => Store(unchecked((ulong)(long)Cast2float(From32(0x46ffff80u))));

    static short Cast2double(double value) => checked((short)value);

    public static int F2double0() =>
        Store(unchecked((ulong)(long)Cast2double(From64(0x400d99999999999aUL))));

    public static int F2double1() =>
        Store(unchecked((ulong)(long)Cast2double(From64(0xbfeccccccccccccdUL))));

    public static int F2double2() =>
        Store(unchecked((ulong)(long)Cast2double(From64(0xc0e0000000000000UL))));

    public static int F2double3() =>
        Store(unchecked((ulong)(long)Cast2double(From64(0x40e0000000000000UL))));

    public static int F2double4() =>
        Store(unchecked((ulong)(long)Cast2double(From64(0xc0e0001800000000UL))));

    public static int F2double5() =>
        Store(unchecked((ulong)(long)Cast2double(From64(0xc0e0002000000000UL))));

    public static int F2double6() =>
        Store(unchecked((ulong)(long)Cast2double(From64(0x40dffff000000000UL))));

    static ushort Cast3long(long value) => checked((ushort)value);

    public static int I3long0() => Store((ulong)Cast3long(0L));

    public static int I3long1() => Store((ulong)Cast3long(65535L));

    public static int I3long2() => Store((ulong)Cast3long(-1L));

    public static int I3long3() => Store((ulong)Cast3long(65536L));

    static ushort Cast3ulong(ulong value) => checked((ushort)value);

    public static int I3ulong0() => Store((ulong)Cast3ulong(0x0000000000000000UL));

    public static int I3ulong1() => Store((ulong)Cast3ulong(0x000000000000ffffUL));

    public static int I3ulong2() => Store((ulong)Cast3ulong(0x0000000000010000UL));

    static ushort Cast3float(float value) => checked((ushort)value);

    public static int F3float0() => Store((ulong)Cast3float(From32(0x406ccccdu)));

    public static int F3float1() => Store((ulong)Cast3float(From32(0xbf666666u)));

    public static int F3float2() => Store((ulong)Cast3float(From32(0x00000000u)));

    public static int F3float3() => Store((ulong)Cast3float(From32(0x47800000u)));

    public static int F3float4() => Store((ulong)Cast3float(From32(0xbf400000u)));

    public static int F3float5() => Store((ulong)Cast3float(From32(0xbf800000u)));

    public static int F3float6() => Store((ulong)Cast3float(From32(0x477fffc0u)));

    static ushort Cast3double(double value) => checked((ushort)value);

    public static int F3double0() => Store((ulong)Cast3double(From64(0x400d99999999999aUL)));

    public static int F3double1() => Store((ulong)Cast3double(From64(0xbfeccccccccccccdUL)));

    public static int F3double2() => Store((ulong)Cast3double(From64(0x0000000000000000UL)));

    public static int F3double3() => Store((ulong)Cast3double(From64(0x40f0000000000000UL)));

    public static int F3double4() => Store((ulong)Cast3double(From64(0xbfe8000000000000UL)));

    public static int F3double5() => Store((ulong)Cast3double(From64(0xbff0000000000000UL)));

    public static int F3double6() => Store((ulong)Cast3double(From64(0x40effff800000000UL)));

    static int Cast4long(long value) => checked((int)value);

    public static int I4long0() => Store(unchecked((ulong)(long)Cast4long(-2147483648L)));

    public static int I4long1() => Store(unchecked((ulong)(long)Cast4long(2147483647L)));

    public static int I4long2() => Store(unchecked((ulong)(long)Cast4long(-2147483649L)));

    public static int I4long3() => Store(unchecked((ulong)(long)Cast4long(2147483648L)));

    static int Cast4ulong(ulong value) => checked((int)value);

    public static int I4ulong0() => Store(unchecked((ulong)(long)Cast4ulong(0x0000000000000000UL)));

    public static int I4ulong1() => Store(unchecked((ulong)(long)Cast4ulong(0x000000007fffffffUL)));

    public static int I4ulong2() => Store(unchecked((ulong)(long)Cast4ulong(0x0000000080000000UL)));

    static int Cast4float(float value) => checked((int)value);

    public static int F4float0() => Store(unchecked((ulong)(long)Cast4float(From32(0x406ccccdu))));

    public static int F4float1() => Store(unchecked((ulong)(long)Cast4float(From32(0xbf666666u))));

    public static int F4float2() => Store(unchecked((ulong)(long)Cast4float(From32(0xcf000000u))));

    public static int F4float3() => Store(unchecked((ulong)(long)Cast4float(From32(0x4f000000u))));

    static int Cast4double(double value) => checked((int)value);

    public static int F4double0() =>
        Store(unchecked((ulong)(long)Cast4double(From64(0x400d99999999999aUL))));

    public static int F4double1() =>
        Store(unchecked((ulong)(long)Cast4double(From64(0xbfeccccccccccccdUL))));

    public static int F4double2() =>
        Store(unchecked((ulong)(long)Cast4double(From64(0xc1e0000000000000UL))));

    public static int F4double3() =>
        Store(unchecked((ulong)(long)Cast4double(From64(0x41e0000000000000UL))));

    public static int F4double4() =>
        Store(unchecked((ulong)(long)Cast4double(From64(0xc1e0000000180000UL))));

    public static int F4double5() =>
        Store(unchecked((ulong)(long)Cast4double(From64(0xc1e0000000200000UL))));

    public static int F4double6() =>
        Store(unchecked((ulong)(long)Cast4double(From64(0x41dffffffff00000UL))));

    static uint Cast5long(long value) => checked((uint)value);

    public static int I5long0() => Store((ulong)Cast5long(0L));

    public static int I5long1() => Store((ulong)Cast5long(4294967295L));

    public static int I5long2() => Store((ulong)Cast5long(-1L));

    public static int I5long3() => Store((ulong)Cast5long(4294967296L));

    static uint Cast5ulong(ulong value) => checked((uint)value);

    public static int I5ulong0() => Store((ulong)Cast5ulong(0x0000000000000000UL));

    public static int I5ulong1() => Store((ulong)Cast5ulong(0x00000000ffffffffUL));

    public static int I5ulong2() => Store((ulong)Cast5ulong(0x0000000100000000UL));

    static uint Cast5float(float value) => checked((uint)value);

    public static int F5float0() => Store((ulong)Cast5float(From32(0x406ccccdu)));

    public static int F5float1() => Store((ulong)Cast5float(From32(0xbf666666u)));

    public static int F5float2() => Store((ulong)Cast5float(From32(0x00000000u)));

    public static int F5float3() => Store((ulong)Cast5float(From32(0x4f800000u)));

    public static int F5float4() => Store((ulong)Cast5float(From32(0xbf400000u)));

    public static int F5float5() => Store((ulong)Cast5float(From32(0xbf800000u)));

    static uint Cast5double(double value) => checked((uint)value);

    public static int F5double0() => Store((ulong)Cast5double(From64(0x400d99999999999aUL)));

    public static int F5double1() => Store((ulong)Cast5double(From64(0xbfeccccccccccccdUL)));

    public static int F5double2() => Store((ulong)Cast5double(From64(0x0000000000000000UL)));

    public static int F5double3() => Store((ulong)Cast5double(From64(0x41f0000000000000UL)));

    public static int F5double4() => Store((ulong)Cast5double(From64(0xbfe8000000000000UL)));

    public static int F5double5() => Store((ulong)Cast5double(From64(0xbff0000000000000UL)));

    public static int F5double6() => Store((ulong)Cast5double(From64(0x41effffffff80000UL)));

    static long Cast6long(long value) => checked((long)value);

    public static int I6long0() => Store(unchecked((ulong)(long)Cast6long(long.MinValue)));

    public static int I6long1() => Store(unchecked((ulong)(long)Cast6long(9223372036854775807L)));

    static long Cast6ulong(ulong value) => checked((long)value);

    public static int I6ulong0() => Store(unchecked((ulong)(long)Cast6ulong(0x0000000000000000UL)));

    public static int I6ulong1() => Store(unchecked((ulong)(long)Cast6ulong(0x7fffffffffffffffUL)));

    public static int I6ulong2() => Store(unchecked((ulong)(long)Cast6ulong(0x8000000000000000UL)));

    static long Cast6float(float value) => checked((long)value);

    public static int F6float0() => Store(unchecked((ulong)(long)Cast6float(From32(0x406ccccdu))));

    public static int F6float1() => Store(unchecked((ulong)(long)Cast6float(From32(0xbf666666u))));

    public static int F6float2() => Store(unchecked((ulong)(long)Cast6float(From32(0xdf000000u))));

    public static int F6float3() => Store(unchecked((ulong)(long)Cast6float(From32(0x5f000000u))));

    public static int F6float4() => Store(unchecked((ulong)(long)Cast6float(From32(0x7fc00000u))));

    public static int F6float5() => Store(unchecked((ulong)(long)Cast6float(From32(0x7f800000u))));

    public static int F6float6() => Store(unchecked((ulong)(long)Cast6float(From32(0xff800000u))));

    static long Cast6double(double value) => checked((long)value);

    public static int F6double0() =>
        Store(unchecked((ulong)(long)Cast6double(From64(0x400d99999999999aUL))));

    public static int F6double1() =>
        Store(unchecked((ulong)(long)Cast6double(From64(0xbfeccccccccccccdUL))));

    public static int F6double2() =>
        Store(unchecked((ulong)(long)Cast6double(From64(0xc3e0000000000000UL))));

    public static int F6double3() =>
        Store(unchecked((ulong)(long)Cast6double(From64(0x43e0000000000000UL))));

    public static int F6double4() =>
        Store(unchecked((ulong)(long)Cast6double(From64(0x7ff8000000000000UL))));

    public static int F6double5() =>
        Store(unchecked((ulong)(long)Cast6double(From64(0x7ff0000000000000UL))));

    public static int F6double6() =>
        Store(unchecked((ulong)(long)Cast6double(From64(0xfff0000000000000UL))));

    static ulong Cast7long(long value) => checked((ulong)value);

    public static int I7long0() => Store((ulong)Cast7long(0L));

    public static int I7long1() => Store((ulong)Cast7long(-1L));

    static ulong Cast7ulong(ulong value) => checked((ulong)value);

    public static int I7ulong0() => Store((ulong)Cast7ulong(0x0000000000000000UL));

    public static int I7ulong1() => Store((ulong)Cast7ulong(0xffffffffffffffffUL));

    static ulong Cast7float(float value) => checked((ulong)value);

    public static int F7float0() => Store((ulong)Cast7float(From32(0x406ccccdu)));

    public static int F7float1() => Store((ulong)Cast7float(From32(0xbf666666u)));

    public static int F7float2() => Store((ulong)Cast7float(From32(0x00000000u)));

    public static int F7float3() => Store((ulong)Cast7float(From32(0x5f800000u)));

    public static int F7float4() => Store((ulong)Cast7float(From32(0x7fc00000u)));

    public static int F7float5() => Store((ulong)Cast7float(From32(0x7f800000u)));

    public static int F7float6() => Store((ulong)Cast7float(From32(0xff800000u)));

    static ulong Cast7double(double value) => checked((ulong)value);

    public static int F7double0() => Store((ulong)Cast7double(From64(0x400d99999999999aUL)));

    public static int F7double1() => Store((ulong)Cast7double(From64(0xbfeccccccccccccdUL)));

    public static int F7double2() => Store((ulong)Cast7double(From64(0x0000000000000000UL)));

    public static int F7double3() => Store((ulong)Cast7double(From64(0x43f0000000000000UL)));

    public static int F7double4() => Store((ulong)Cast7double(From64(0x7ff8000000000000UL)));

    public static int F7double5() => Store((ulong)Cast7double(From64(0x7ff0000000000000UL)));

    public static int F7double6() => Store((ulong)Cast7double(From64(0xfff0000000000000UL)));

    static nint Cast8long(long value) => checked((nint)value);

    public static int I8long0() => Store(unchecked((ulong)(long)Cast8long(-32768L)));

    public static int I8long1() => Store(unchecked((ulong)(long)Cast8long(32767L)));

    public static int I8long2() => Store(unchecked((ulong)(long)Cast8long(-32769L)));

    public static int I8long3() => Store(unchecked((ulong)(long)Cast8long(32768L)));

    static nint Cast8ulong(ulong value) => checked((nint)value);

    public static int I8ulong0() => Store(unchecked((ulong)(long)Cast8ulong(0x0000000000000000UL)));

    public static int I8ulong1() => Store(unchecked((ulong)(long)Cast8ulong(0x0000000000007fffUL)));

    public static int I8ulong2() => Store(unchecked((ulong)(long)Cast8ulong(0x0000000000008000UL)));

    static nint Cast8float(float value) => checked((nint)value);

    public static int F8float0() => Store(unchecked((ulong)(long)Cast8float(From32(0x406ccccdu))));

    public static int F8float1() => Store(unchecked((ulong)(long)Cast8float(From32(0xbf666666u))));

    public static int F8float2() => Store(unchecked((ulong)(long)Cast8float(From32(0xc7000000u))));

    public static int F8float3() => Store(unchecked((ulong)(long)Cast8float(From32(0x47000000u))));

    public static int F8float4() => Store(unchecked((ulong)(long)Cast8float(From32(0xc70000c0u))));

    public static int F8float5() => Store(unchecked((ulong)(long)Cast8float(From32(0xc7000100u))));

    public static int F8float6() => Store(unchecked((ulong)(long)Cast8float(From32(0x46ffff80u))));

    static nint Cast8double(double value) => checked((nint)value);

    public static int F8double0() =>
        Store(unchecked((ulong)(long)Cast8double(From64(0x400d99999999999aUL))));

    public static int F8double1() =>
        Store(unchecked((ulong)(long)Cast8double(From64(0xbfeccccccccccccdUL))));

    public static int F8double2() =>
        Store(unchecked((ulong)(long)Cast8double(From64(0xc0e0000000000000UL))));

    public static int F8double3() =>
        Store(unchecked((ulong)(long)Cast8double(From64(0x40e0000000000000UL))));

    public static int F8double4() =>
        Store(unchecked((ulong)(long)Cast8double(From64(0xc0e0001800000000UL))));

    public static int F8double5() =>
        Store(unchecked((ulong)(long)Cast8double(From64(0xc0e0002000000000UL))));

    public static int F8double6() =>
        Store(unchecked((ulong)(long)Cast8double(From64(0x40dffff000000000UL))));

    static nuint Cast9long(long value) => checked((nuint)value);

    public static int I9long0() => Store((ulong)Cast9long(0L));

    public static int I9long1() => Store((ulong)Cast9long(65535L));

    public static int I9long2() => Store((ulong)Cast9long(-1L));

    public static int I9long3() => Store((ulong)Cast9long(65536L));

    static nuint Cast9ulong(ulong value) => checked((nuint)value);

    public static int I9ulong0() => Store((ulong)Cast9ulong(0x0000000000000000UL));

    public static int I9ulong1() => Store((ulong)Cast9ulong(0x000000000000ffffUL));

    public static int I9ulong2() => Store((ulong)Cast9ulong(0x0000000000010000UL));

    static nuint Cast9float(float value) => checked((nuint)value);

    public static int F9float0() => Store((ulong)Cast9float(From32(0x406ccccdu)));

    public static int F9float1() => Store((ulong)Cast9float(From32(0xbf666666u)));

    public static int F9float2() => Store((ulong)Cast9float(From32(0x00000000u)));

    public static int F9float3() => Store((ulong)Cast9float(From32(0x47800000u)));

    public static int F9float4() => Store((ulong)Cast9float(From32(0xbf400000u)));

    public static int F9float5() => Store((ulong)Cast9float(From32(0xbf800000u)));

    public static int F9float6() => Store((ulong)Cast9float(From32(0x477fffc0u)));

    static nuint Cast9double(double value) => checked((nuint)value);

    public static int F9double0() => Store((ulong)Cast9double(From64(0x400d99999999999aUL)));

    public static int F9double1() => Store((ulong)Cast9double(From64(0xbfeccccccccccccdUL)));

    public static int F9double2() => Store((ulong)Cast9double(From64(0x0000000000000000UL)));

    public static int F9double3() => Store((ulong)Cast9double(From64(0x40f0000000000000UL)));

    public static int F9double4() => Store((ulong)Cast9double(From64(0xbfe8000000000000UL)));

    public static int F9double5() => Store((ulong)Cast9double(From64(0xbff0000000000000UL)));

    public static int F9double6() => Store((ulong)Cast9double(From64(0x40effff800000000UL)));

    static ulong NativeUnsigned(nint value) => checked((ulong)value);

    static long UNativeSigned(nuint value) => checked((long)value);

    static int UIntSigned(uint value) => checked((int)value);

    static void Discard(int value)
    {
        _ = checked((byte)value);
    }

    public static int NativeNegative() => Store(NativeUnsigned((nint)(-1)));

    public static int NativePositive() => Store(NativeUnsigned((nint)123));

    public static int UNativePositive() => Store((ulong)UNativeSigned((nuint)65535));

    public static int UIntOverflow() => Store((ulong)UIntSigned(uint.MaxValue));

    public static int DiscardValid()
    {
        Discard(255);
        return Store(42);
    }

    public static int DiscardOverflow()
    {
        Discard(256);
        return Store(42);
    }

    static int Loop(int limit)
    {
        int sum = 0;
        for (int i = 0; i < limit; i++)
            sum += checked((byte)i);
        return sum;
    }

    public static int LoopValid() => Store((ulong)Loop(4));

    public static int LoopOverflow() => Store((ulong)Loop(257));
}
