using Koh.GameBoy;

public static unsafe class Program
{
    public static void Main()
    {
        foreach (int count in new[] { 0, 1, 2, 3, 4, 5, 7, 15, 16, 17, 255, 256, 257, 300 })
            for (int srcOffset = 0; srcOffset < 4; srcOffset++)
            for (int dstOffset = 0; dstOffset < 4; dstOffset++)
            {
                byte[] source = new byte[340];
                byte[] destination = new byte[340];
                for (int i = 0; i < source.Length; i++)
                    source[i] = (byte)(i ^ (i >> 4));
                System.Array.Fill(destination, (byte)0x5A);
                fixed (byte* src = source)
                fixed (byte* dst = destination)
                    Mem.Copy(dst + 16 + dstOffset, src + srcOffset, (ushort)count);
                for (int i = 0; i < destination.Length; i++)
                {
                    byte expected =
                        i >= 16 + dstOffset && i < 16 + dstOffset + count
                            ? (byte)(
                                (i - 16 - dstOffset + srcOffset)
                                ^ ((i - 16 - dstOffset + srcOffset) >> 4)
                            )
                            : (byte)0x5A;
                    if (destination[i] != expected || source[i] != (byte)(i ^ (i >> 4)))
                        throw new System.InvalidOperationException(
                            $"{count}:{srcOffset}:{dstOffset}:{i}"
                        );
                }
            }
        System.Console.WriteLine("224 copies OK");
    }

    public static int Main0() => Run(0);

    public static int Main1() => Run(1);

    public static int Main2() => Run(2);

    public static int Main3() => Run(3);

    public static int Bulk0() => Bulk(0);

    public static int Bulk1() => Bulk(1);

    public static int Bulk2() => Bulk(2);

    public static int Bulk3() => Bulk(3);

    public static int Cost64() => Cost(64);

    public static int Cost256() => Cost(256);

    public static int Cost1920() => Cost(1920);

    private static int Cost(ushort count)
    {
        byte* destination = Mem.Alloc(2000);
        byte* source = Mem.Alloc(2000);
        Mem.Copy(destination, source, count);
        return 42;
    }

    public static int CopyFillIntegration()
    {
        byte* destination = Mem.Alloc(4);
        byte* source = Mem.Alloc(4);
        Mem.Copy(destination, source, 4);
        Mem.Fill(destination, 1, 4);
        return 42;
    }

    public static int OverlapSeven()
    {
        byte* p = (byte*)0xD100;
        for (int i = 0; i < 10; i++)
            p[i] = (byte)i;
        Mem.Copy(p, p + 3, 7);
        return 42;
    }

    public static int ForwardOverlap()
    {
        int value = 0x12345678;
        Mem.Copy((byte*)&value + 1, (byte*)&value, 3);
        return value;
    }

    public static int BackwardOverlap()
    {
        int value = 0x12345678;
        Mem.Copy((byte*)&value, (byte*)&value + 1, 3);
        return value;
    }

    public static int SameAddress()
    {
        int value = 0x12345678;
        Mem.Copy((byte*)&value, (byte*)&value, 4);
        return value;
    }

    public static int LocalNoOp()
    {
        int value = 0x12345678;
        Mem.Copy((byte*)&value, (byte*)0xFF70, 0);
        return value;
    }

    public static int WrapDestination()
    {
        Hardware.BGP = 0x42;
        Mem.Copy((byte*)0xFFFE, (byte*)0x2000, 4);
        Hardware.BGP = 0x43;
        return 42;
    }

    public static int WrapSource()
    {
        *(byte*)0xFFFE = 0x11;
        *(byte*)0xFFFF = 4;
        Hardware.BGP = 0x42;
        Mem.Copy((byte*)0xD100, (byte*)0xFFFE, 4);
        Hardware.BGP = 0x43;
        return 42;
    }

    public static int ZeroBus()
    {
        Hardware.BGP = 0x42;
        Mem.Copy((byte*)0xFFFE, (byte*)0xFFFE, 0);
        Hardware.BGP = 0x43;
        return 42;
    }

    private static void CopyBank() => Mem.Copy((byte*)0xFF6F, (byte*)0xD200, 2);

    public static int BankTwoCopy()
    {
        *(byte*)0xFF70 = 2;
        Mem.Copy((byte*)0xD100, (byte*)0x2000, 4);
        byte bank = *(byte*)0xFF70;
        return bank == 255 || (bank & 7) == 2 ? 42 : -1;
    }

    public static int BadBank()
    {
        byte[] array = new byte[1];
        fixed (byte* p = array)
        {
            Mem.Copy((byte*)0xFF6F, (byte*)0xD200, 2);
            return *p;
        }
    }

    public static int IndirectBadBank()
    {
        byte[] array = new byte[1];
        fixed (byte* p = array)
        {
            CopyBank();
            return *p;
        }
    }

    public static int PinnedCopy()
    {
        *(byte*)0xFF70 = 2;
        byte[] array = new byte[2];
        array[0] = 42;
        array[1] = 17;
        int value;
        fixed (byte* p = array)
        {
            Mem.Copy((byte*)0xD100, p, 2);
            Mem.Copy((byte*)0xFF70, (byte*)0xFF70, 0);
            value = *p;
        }
        byte bank = *(byte*)0xFF70;
        return bank == 255 || (bank & 7) == 2 ? value : -1;
    }

    [KohIntrinsic("memcopy", 0)]
    private static void Address(byte* dst, byte* src, ushort count) => throw null;

    [KohIntrinsic("memcopy")]
    private static int Return(byte* dst, byte* src, ushort count) => throw null;

    [KohIntrinsic("memcopy")]
    private static void Destination(void* dst, byte* src, ushort count) => throw null;

    [KohIntrinsic("memcopy")]
    private static void Source(byte* dst, void* src, ushort count) => throw null;

    [KohIntrinsic("memcopy")]
    private static void Count(byte* dst, byte* src, int count) => throw null;

    public static void BadAddress() => Address((byte*)0xD100, (byte*)0xD200, 0);

    public static int BadReturn() => Return((byte*)0xD100, (byte*)0xD200, 0);

    public static void BadDestination() => Destination((byte*)0xD100, (byte*)0xD200, 0);

    public static void BadSource() => Source((byte*)0xD100, (byte*)0xD200, 0);

    public static void BadCount() => Count((byte*)0xD100, (byte*)0xD200, 0);

    private static byte* Part(byte* destination, byte* source, ushort count)
    {
        Mem.Copy(destination, source, count);
        return destination + count + 16;
    }

    private static int Run(byte offset)
    {
        byte* source = (byte*)(0x2000 + ((offset + 1) & 3));
        byte* p = (byte*)(0xD100 + offset);
        p = Part(p, source, 0);
        p = Part(p, source, 1);
        p = Part(p, source, 2);
        p = Part(p, source, 3);
        p = Part(p, source, 4);
        p = Part(p, source, 5);
        p = Part(p, source, 7);
        p = Part(p, source, 15);
        p = Part(p, source, 16);
        p = Part(p, source, 17);
        p = Part(p, source, 255);
        p = Part(p, source, 256);
        p = Part(p, source, 257);
        Part(p, source, 300);
        Mem.Copy((byte*)0xDFFC, source, 4);
        return 42;
    }

    private static int Bulk(byte offset)
    {
        Mem.Copy((byte*)(0xD080 + offset), (byte*)(0x2000 + ((offset + 1) & 3)), 3840);
        return 42;
    }
}
