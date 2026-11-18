using Koh.GameBoy;

public static unsafe class Program
{
    public static int ClearMap()
    {
        Lcd.Off();
        bool color = Cgb.IsColor();
        for (byte bank = 0; bank < (color ? 2 : 1); bank++)
        {
            Cgb.SelectVramBank(bank);
            for (int index = -1; index <= 1024; index++)
                Gb.TileMap[index] = 0x5A;
            Tilemap.Clear(bank == 0 ? (byte)0xA7 : (byte)0x3C);
        }
        for (byte bank = 0; bank < (color ? 2 : 1); bank++)
        {
            Cgb.SelectVramBank(bank);
            if (Gb.TileMap[-1] != 0x5A || Gb.TileMap[1024] != 0x5A)
                return 1;
            for (int index = 0; index < 1024; index++)
                if (Gb.TileMap[index] != (bank == 0 ? 0xA7 : 0x3C))
                    return 2;
        }
        return 42;
    }

    public static void Main()
    {
        foreach (int count in new[] { 0, 1, 2, 3, 4, 5, 7, 15, 16, 17, 255, 256, 257, 300 })
            for (int offset = 0; offset < 4; offset++)
            {
                byte[] buffer = new byte[340];
                System.Array.Fill(buffer, (byte)0x5A);
                fixed (byte* p = buffer)
                    Mem.Fill(p + 16 + offset, 0xA7, (ushort)count);
                for (int index = 0; index < buffer.Length; index++)
                    if (
                        buffer[index]
                        != (index >= 16 + offset && index < 16 + offset + count ? 0xA7 : 0x5A)
                    )
                        throw new System.InvalidOperationException($"{count}:{offset}:{index}");
            }
        System.Console.WriteLine("56 fills OK");
    }

    [KohIntrinsic("memfill", 0)]
    private static void Address(byte* p, byte value, ushort count) =>
        throw new System.NotSupportedException();

    [KohIntrinsic("memfill")]
    private static int Return(byte* p, byte value, ushort count) =>
        throw new System.NotSupportedException();

    [KohIntrinsic("memfill")]
    private static void Pointer(void* p, byte value, ushort count) =>
        throw new System.NotSupportedException();

    [KohIntrinsic("memfill")]
    private static void Value(byte* p, int value, ushort count) =>
        throw new System.NotSupportedException();

    [KohIntrinsic("memfill")]
    private static void Count(byte* p, byte value, int count) =>
        throw new System.NotSupportedException();

    public static void BadAddress() => Address((byte*)0xD100, 1, 0);

    public static int BadReturn() => Return((byte*)0xD100, 1, 0);

    public static void BadPointer() => Pointer((byte*)0xD100, 1, 0);

    public static void BadValue() => Value((byte*)0xD100, 1, 0);

    public static void BadCount() => Count((byte*)0xD100, 1, 0);

    private static void FillBank() => Mem.Fill((byte*)0xFF6F, 2, 2);

    public static int BadBank()
    {
        byte[] array = new byte[1];
        fixed (byte* p = array)
        {
            Mem.Fill((byte*)0xFF6F, 2, 2);
            return *p;
        }
    }

    public static int IndirectBadBank()
    {
        byte[] array = new byte[1];
        fixed (byte* p = array)
        {
            FillBank();
            return *p;
        }
    }

    public static int ZeroBank()
    {
        byte[] array = new byte[1];
        array[0] = 42;
        fixed (byte* p = array)
        {
            Mem.Fill((byte*)0xFF70, 2, 0);
            return *p;
        }
    }

    public static int PinnedFill()
    {
        byte[] array = new byte[1];
        array[0] = 42;
        fixed (byte* p = array)
        {
            Mem.Fill((byte*)0xD100, 2, 3);
            return *p;
        }
    }

    public static int Main0() => Run(0);

    public static int Main1() => Run(1);

    public static int Main2() => Run(2);

    public static int Main3() => Run(3);

    public static int LocalAlias()
    {
        int value = 0x12345678;
        Mem.Fill((byte*)&value, 0xA7, 3);
        return value;
    }

    public static int LocalNoOp()
    {
        int value = 0x12345678;
        Mem.Fill((byte*)&value, 0xA7, 0);
        return value;
    }

    public static int Wrap()
    {
        Hardware.BGP = 0x42;
        Mem.Fill((byte*)0xFFFE, 0xA7, 4);
        Hardware.BGP = 0x43;
        return 42;
    }

    private static byte* Part(byte* destination, byte value, ushort count)
    {
        Mem.Fill(destination, value, count);
        return destination + count + 16;
    }

    private static int Run(byte offset)
    {
        byte* p = (byte*)(0xD100 + offset);
        p = Part(p, 0xAA, 0);
        p = Part(p, 0xBB, 1);
        p = Part(p, 0x80, 2);
        p = Part(p, 0x7F, 3);
        p = Part(p, 0xCC, 4);
        p = Part(p, 0, 5);
        p = Part(p, 0xCC, 7);
        p = Part(p, 0xFF, 15);
        p = Part(p, 0x19, 16);
        p = Part(p, 0xE7, 17);
        p = Part(p, 0x18, 255);
        p = Part(p, 0x80, 256);
        p = Part(p, 0xEF, 257);
        Part(p, 0x7E, 300);
        Mem.Fill((byte*)0xDFFC, 0x7F, 4);
        return 42;
    }
}
