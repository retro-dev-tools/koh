public static unsafe class Program
{
    static int Read(byte* address) => *address;

    public static int Main()
    {
        *(long*)0xC021 = unchecked((long)0xFEDCBA9876543210UL);
        if (*(ulong*)0xC021 != 0xFEDCBA9876543210UL)
            return 1;
        if (*(uint*)0xC021 != 0x76543210U)
            return 2;
        if (*(ushort*)0xC023 != 0x7654)
            return 3;
        if (Read((byte*)0xC028) != 0xFE)
            return 4;
        if (*(sbyte*)0xC028 != -2)
            return 5;
        if (*(short*)0xC027 != -292)
            return 6;
        *(byte*)0xC022 = unchecked((byte)0x1AB);
        if (*(uint*)0xC021 != 0x7654AB10U)
            return 7;
        *(ushort*)0xC023 = unchecked((ushort)0x18001);
        if (*(short*)0xC023 != -32767)
            return 8;
        *(uint*)0xC021 = 0x89ABCDEFU;
        if (*(int*)0xC021 != unchecked((int)0x89ABCDEFU))
            return 9;
        *(nuint*)0xC029 = 0xFEDC;
        if (*(nuint*)0xC029 != 0xFEDC)
            return 10;
        return 42;
    }
}
