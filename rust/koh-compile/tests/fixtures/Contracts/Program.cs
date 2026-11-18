using Koh.GameBoy;

public static class Program
{
#if ADDRESS
    [KohIntrinsic("register", 0x10000)]
    public static byte Broken() => 0;

    public static int Main() => Broken();
#elif SIGNATURE
    [KohIntrinsic("register", 0xFF43)]
    public static int Broken() => 0;

    public static int Main() => Broken();
#elif NULL
    [KohIntrinsic(null!)]
    public static int Main() => 42;
#elif DUPLICATE
    [KohIntrinsic("register", 0xFF43)]
    public static byte Register
    {
        [KohIntrinsic("register", 0xFF43)]
        get => 0;
    }

    public static int Main() => Register;
#elif REGION
    [KohIntrinsic("region", 0x8000)]
    public static int Register => 0;

    public static int Main() => Register;
#elif INTERRUPT
    [Interrupt("VBlank")]
    public static int UncalledHandler() => 42;

    public static int Main() => 42;
#elif PROPERTY
    [KohIntrinsic("unsupported-register", 0xFF40)]
    public static byte Register => 0;

    public static int Main() => Register;
#else
    [KohIntrinsic("unsupported-cpu-control")]
    public static void DesktopNoOp() { }

    public static int Main()
    {
        DesktopNoOp();
        return 42;
    }
#endif
}
