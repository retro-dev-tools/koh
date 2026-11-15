using Koh.GameBoy;

public static unsafe class Program
{
    [KohIntrinsic("register", 0xFF70)]
    static byte Bank
    {
        get => 0;
        set { }
    }

    public static int BankedCopy()
    {
        Bank = 2;
        int result = Copy();
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int BankedEmpty()
    {
        Bank = 2;
        int result = Empty();
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int BankedNull()
    {
        Bank = 2;
        int result = Null();
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int BankedNested()
    {
        Bank = 2;
        int result = Nested();
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int BankedRecursive()
    {
        Bank = 2;
        int result = Recursive();
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int BankedLoop()
    {
        Bank = 2;
        return LoopCore(true);
    }

    public static int BankedUnbanked()
    {
        Bank = 2;
        byte value = 7;
        return Write(ref value, true);
    }

    public static int Copy()
    {
        Hardware.LCDC = 0;
        byte[] data = { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0 };
        fixed (byte* source = &data[0])
        {
            Mem.Copy(Gb.Vram, source, (ushort)data.Length);
            source[5] = 0xA5;
        }
        fixed (byte* source = data)
            Mem.Copy(Gb.Vram + 8, source, (ushort)data.Length);
        if (*(uint*)Gb.Vram != 0x78563412 || *(uint*)(Gb.Vram + 4) != 0xF0DEBC9A)
            return -2;
        if (*(uint*)(Gb.Vram + 12) != 0xF0DEA59A || data[5] != 0xA5)
            return -3;
        return 42;
    }

    public static int Empty()
    {
        byte[] data = new byte[0];
        fixed (byte* pointer = data)
            return pointer == null ? 42 : -1;
    }

    public static int Null()
    {
        byte[] data = null;
        fixed (byte* pointer = data)
            return pointer == null ? 42 : -1;
    }

    public static int Nested()
    {
        byte[] a = { 11, 22 };
        ushort[] b = { 0x1234 };
        fixed (byte* first = &a[0])
        fixed (ushort* second = &b[0])
        {
            *first = (byte)*second;
            *second = 0xABCD;
            if (first[1] != 22 || first[0] != 0x34)
                return -1;
        }
        return a[0] == 0x34 && b[0] == 0xABCD ? 42 : -2;
    }

    public static int Recursive() => Recurse(4);

    public static int BadBank()
    {
        byte[] data = { 7 };
        fixed (byte* pointer = data)
        {
            Bank = 2;
            return *pointer;
        }
    }

    public static int IndirectBadBank()
    {
        byte[] data = { 7 };
        fixed (byte* pointer = data)
        {
            ChangeBank();
            return *pointer;
        }
    }

    static void ChangeBank() => Bank = 2;

    public static int Loop() => LoopCore(false);

    static int LoopCore(bool color)
    {
        byte[] data = new byte[1];
        for (int i = 0; i < 5; i++)
        {
            fixed (byte* pointer = data)
            {
                if (color && (Bank & 7) != 1)
                    return -1;
                (*pointer)++;
            }
            if (color && (Bank & 7) != 2)
                return -2;
        }
        return data[0];
    }

    public static int Unbanked()
    {
        byte value = 7;
        return Write(ref value, false);
    }

    static int Write(ref byte value, bool color)
    {
        fixed (byte* pointer = &value)
        {
            if (color && (Bank & 7) != 2)
                return -1;
            *pointer = 42;
        }
        return value;
    }

    static int Recurse(int depth)
    {
        byte[] data = { (byte)(depth + 3) };
        fixed (byte* pointer = data)
        {
            int child = depth == 0 ? 0 : Recurse(depth - 1);
            return child + *pointer;
        }
    }
}
