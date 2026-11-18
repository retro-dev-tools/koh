using System.Runtime.InteropServices;
using Koh.GameBoy;

public static unsafe class Program
{
    [StructLayout(LayoutKind.Sequential, Size = 200)]
    private struct Padding
    {
        public long Value;
    }

    [KohIntrinsic("vblankcopy")]
    private static ushort Copy(byte* destination, byte* source, ushort count) =>
        throw new System.PlatformNotSupportedException();

    public static int Main() => Copy((byte*)0x8001, (byte*)0x2000, 257);

    public static int Zero() => Copy((byte*)0x8001, (byte*)0x2000, 0);

    public static int One() => Copy((byte*)0x8001, (byte*)0x2000, 1);

    public static int Seventeen() => Copy((byte*)0x8001, (byte*)0x2000, 17);

    public static int Large()
    {
        byte* scratch = stackalloc byte[200];
        scratch[0] = 0x42;
        scratch[199] = 0x99;
        Padding padding = default;
        padding.Value = 0x12345678ABCDEF;
        int count = Copy((byte*)0x8001, (byte*)0x2000, 17);
        return padding.Value == 0x12345678ABCDEF && scratch[0] == 0x42 && scratch[199] == 0x99
            ? count
            : -1;
    }

    [KohIntrinsic("vblankcopy")]
    private static void BadReturn(byte* destination, byte* source, ushort count) { }

    public static void RejectReturn() => BadReturn((byte*)0x8001, (byte*)0x2000, 1);

    [KohIntrinsic("vblankcopy", 0x8000)]
    private static ushort BadAddress(byte* destination, byte* source, ushort count) => 0;

    public static int RejectAddress() => BadAddress((byte*)0x8001, (byte*)0x2000, 1);
}
