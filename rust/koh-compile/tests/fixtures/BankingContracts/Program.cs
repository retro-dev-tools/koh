public static unsafe class Program
{
    public static int Same(int bank)
    {
        *(byte*)0x3000 = 0;
        *(byte*)0x2000 = 3;
        int first = *(byte*)0x7ff0;
        *(byte*)0x2000 = 3;
        return (first << 8) | *(byte*)0x7ff0;
    }

    public static int Different(int bank)
    {
        *(byte*)0x3000 = 0;
        *(byte*)0x2000 = 3;
        int first = *(byte*)0x7ff0;
        *(byte*)0x2000 = 4;
        return (first << 8) | *(byte*)0x7ff0;
    }

    public static int Call(int bank)
    {
        *(byte*)0x3000 = 0;
        *(byte*)0x2000 = 3;
        int first = *(byte*)0x7ff0;
        Change();
        int second = *(byte*)0x7ff0;
        *(byte*)0x2000 = 3;
        return (first << 16) | (second << 8) | *(byte*)0x7ff0;
    }

    private static void Change()
    {
        *(byte*)0x2000 = 4;
    }

    public static int Ordinary(int bank)
    {
        *(byte*)0x3000 = 0;
        *(byte*)0x2000 = 3;
        *(byte*)0xc020 = 42;
        *(byte*)0x2000 = 3;
        return (*(byte*)0x7ff0 << 8) | *(byte*)0xc020;
    }

    public static int OtherRegister(int bank)
    {
        *(byte*)0x3000 = 0;
        *(byte*)0x2000 = 3;
        *(byte*)0x0000 = 0;
        *(byte*)0x2000 = 3;
        return *(byte*)0x7ff0;
    }

    public static int Variable(int bank)
    {
        *(byte*)0x3000 = 0;
        *(byte*)0x2000 = 3;
        int first = *(byte*)0x7ff0;
        *(byte*)0x2000 = (byte)bank;
        int second = *(byte*)0x7ff0;
        *(byte*)0x2000 = 3;
        return (first << 16) | (second << 8) | *(byte*)0x7ff0;
    }

    public static int HighBit(int bank)
    {
        *(byte*)0x3000 = 0;
        *(byte*)0x2000 = 3;
        int first = *(byte*)0x7ff0;
        *(byte*)0x3000 = 1;
        int second = *(byte*)0x7ff0;
        *(byte*)0x3000 = 0;
        *(byte*)0x2000 = 3;
        return (first << 16) | (second << 8) | *(byte*)0x7ff0;
    }
}
