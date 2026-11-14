using Koh.GameBoy;

public static class Program
{
#if INTERRUPT
    [Interrupt("VBlank")]
    public static void UncalledHandler() { }

    public static int Main() => 42;
#elif PROPERTY
    [KohIntrinsic("register", 0xFF40)]
    public static byte Register => 0;

    public static int Main() => Register;
#else
    [KohIntrinsic("halt")]
    public static void DesktopNoOp() { }

    public static int Main()
    {
        DesktopNoOp();
        return 42;
    }
#endif
}
