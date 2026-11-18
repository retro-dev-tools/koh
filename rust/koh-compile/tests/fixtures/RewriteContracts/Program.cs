public static unsafe class Program
{
    public static ulong Carry(ulong x, ulong y) => unchecked(x + y);

    public static ulong Borrow(ulong x, ulong y) => unchecked(x - y);

    public static ulong Shifts(ulong x, int count) => (x << count) ^ (x >> count);

    public static int Adjacent(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        *(byte*)0xc020 = (byte)y;
        return *(byte*)0xc020;
    }

    public static int AcrossArithmetic(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        int value = unchecked(y + 1);
        *(byte*)0xc020 = (byte)value;
        return *(byte*)0xc020;
    }

    public static int Different(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        *(byte*)0xc021 = (byte)y;
        return (*(byte*)0xc020 << 8) | *(byte*)0xc021;
    }

    public static int InterveningRead(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        int before = *(byte*)0xc020;
        *(byte*)0xc020 = (byte)y;
        return (before << 8) | *(byte*)0xc020;
    }

    public static int AliasWrite(int x, int y)
    {
        byte* pointer = (x & 1) == 0 ? (byte*)0xc020 : (byte*)0xc021;
        *(byte*)0xc020 = 0x11;
        *(byte*)0xc021 = 0x22;
        *pointer = (byte)y;
        return (*(byte*)0xc020 << 8) | *(byte*)0xc021;
    }

    public static int Hram(int x, int y)
    {
        *(byte*)0xff80 = (byte)x;
        *(byte*)0xff80 = (byte)y;
        return *(byte*)0xff80;
    }

    public static int Vram(int x, int y)
    {
        *(byte*)0xff40 = 0;
        *(byte*)0x9800 = (byte)x;
        *(byte*)0x9800 = (byte)y;
        return *(byte*)0x9800;
    }

    public static int Join(int x, int y)
    {
        *(byte*)0xc020 = 0x11;
        if (x != 0)
            *(byte*)0xc020 = (byte)y;
        return *(byte*)0xc020;
    }

    public static int ReloadAfterAlu(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        int first = *(byte*)0xc020;
        int value = unchecked(first + y);
        return (*(byte*)0xc020 << 16) | (value & 0xffff);
    }

    public static int ReadPair(int x, int y)
    {
        int first = *(byte*)0xff44;
        int second = *(byte*)0xff44;
        return x;
    }

    public static int Call(int x, int y)
    {
        *(byte*)0xc020 = (byte)x;
        Change(y);
        return *(byte*)0xc020;
    }

    private static void Change(int value)
    {
        *(byte*)0xc020 = (byte)value;
    }

    public static int Tail(int x, int y) => Leaf(x);

    public static int NotTail(int x, int y) => unchecked(Leaf(x) + y);

    public static int ReturnJoin(int x, int y) => x == 0 ? Leaf(y) : unchecked(Leaf(x) + 3);

    private static int Leaf(int value) => unchecked(value + 3);

    public static int PostIncrement(int x, int y)
    {
        byte* pointer = (byte*)0xc020;
        pointer[0] = (byte)x;
        pointer[1] = (byte)y;
        int first = *pointer++;
        return (first << 8) | *pointer;
    }

    public static int IncrementEntry(int x, int y)
    {
        byte* pointer = (byte*)0xc020;
        pointer[0] = (byte)x;
        pointer[1] = (byte)y;
        int first = 0;
        if ((x & 1) != 0)
            first = *pointer;
        pointer++;
        return (first << 8) | *pointer;
    }

    public static int PostDecrement(int x, int y)
    {
        byte* pointer = (byte*)0xc021;
        pointer[-1] = (byte)x;
        pointer[0] = (byte)y;
        *pointer-- = 0x66;
        return *pointer | (pointer[1] << 8);
    }

    public static int BranchStores(int x, int y)
    {
        if (x == 0)
            *(byte*)0xc020 = (byte)y;
        else
            *(byte*)0xc020 = unchecked((byte)(y + 1));
        return *(byte*)0xc020;
    }

    public static int IndexedStore(int raw, int index)
    {
        byte* pointer = (byte*)(ushort)raw;
        pointer[(short)index] = 77;
        return pointer[(short)index];
    }

    public static int IndexedLoad(int raw, int index)
    {
        byte* pointer = (byte*)(ushort)raw + (short)index;
        *pointer = 200;
        return *((byte*)(ushort)raw + (short)index);
    }

    public static int IndexedTwice(int raw, int index)
    {
        byte* pointer = (byte*)(ushort)raw + (short)index;
        *pointer = 9;
        return *pointer + 1;
    }

    public static ulong IndexedDelayed(int raw, int index)
    {
        byte* pointer = (byte*)(ushort)raw + (short)index;
        *pointer = 41;
        int previous = *pointer;
        uint noise = unchecked((uint)Leaf(raw ^ index));
        *pointer = (byte)(previous + 1);
        return ((ulong)noise << 32) | *pointer;
    }

    public static int IndexedReuse(int raw, int index)
    {
        byte* pointer = (byte*)(ushort)raw;
        pointer[(short)index] = 1;
        byte* other = pointer + (short)index;
        *other = 2;
        return *other;
    }
}
