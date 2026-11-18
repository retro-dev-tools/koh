using Koh.GameBoy;

public static unsafe class Program
{
    [KohAligned(256)]
    private static byte[] Aligned;

    [KohIntrinsic("register", 0xFF70)]
    private static byte Bank
    {
        get => 0;
        set { }
    }

    public static int Main()
    {
        byte* a = Mem.Alloc(4);
        byte* b = Mem.Alloc(4);
        a[0] = 11;
        b[0] = 22;
        Hardware.BGP = (byte)(a[0] + b[0] + 9);
        return a != b && a[0] == 11 && b[0] == 22 && Hardware.BGP == 42 ? 42 : 1;
    }

    public static int ResetWithManagedArray()
    {
        byte* first = Mem.Alloc(4);
        first[0] = 5;
        byte[] managed = new byte[3];
        managed[0] = 11;
        managed[2] = 22;
        Mem.Reset();
        byte* second = Mem.Alloc(4);
        second[0] = 42;
        return
            first == second
            && managed.Length == 3
            && managed[0] == 11
            && managed[1] == 0
            && managed[2] == 22
            && second[0] == 42
            ? 42
            : 2;
    }

    public static int BankPreservation()
    {
        Bank = 2;
        byte before = Bank;
        byte* raw = Mem.Alloc(4);
        if (Bank != before)
            return 3;
        Bank = 1;
        raw[0] = 42;
        Bank = 2;
        Mem.Reset();
        if (Bank != before)
            return 4;
        Bank = 1;
        byte* reused = Mem.Alloc(4);
        reused[0] = 43;
        return reused == raw && raw[0] == 43 ? 42 : 5;
    }

    public static int FullArena()
    {
        byte* empty = Mem.Alloc(0);
        byte* full = Mem.Alloc(4096);
        full[0] = 42;
        full[4095] = 99;
        return empty - full == 4096 && Mem.Alloc(0) == full && full[0] == 42 && full[4095] == 99
            ? 42
            : 6;
    }

    public static int ExactSharedCapacity()
    {
        byte[] managed = new byte[4090];
        managed[4089] = 99;
        byte* raw = Mem.Alloc(4);
        raw[0] = 42;
        raw[3] = 43;
        return
            managed.Length == 4090
            && managed[0] == 0
            && managed[4089] == 99
            && raw[0] == 42
            && raw[3] == 43
            ? 42
            : 7;
    }

    public static int ManagedThenRawCollision()
    {
        byte[] managed = new byte[4090];
        managed[4089] = 99;
        Mem.Alloc(5);
        return managed[4089];
    }

    public static int RawThenManagedCollision()
    {
        Mem.Alloc(4093);
        return new byte[2].Length;
    }

    public static int AlignedCollision()
    {
        Aligned = new byte[1];
        Mem.Alloc(3840);
        return Aligned.Length;
    }

    public static int NegativeSize() => (ushort)Mem.Alloc(-1);

    public static int Oversize() => (ushort)Mem.Alloc(4097);

    public static int TruncatedSize() => (ushort)Mem.Alloc(65536);

    public static int Exhausted()
    {
        Mem.Alloc(4096);
        return (ushort)Mem.Alloc(1);
    }

    public static int ResetOnly()
    {
        Mem.Reset();
        return 42;
    }

    [KohIntrinsic("alloc")]
    private static int WrongAllocation(int size) => size;

    [KohIntrinsic("heapreset")]
    private static void WrongReset(int value) { }

    public static int InvalidAllocation() => WrongAllocation(4);

    public static void InvalidReset() => WrongReset(4);
}
