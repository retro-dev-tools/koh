public static class Program
{
#if MODULE_INIT
    [System.Runtime.CompilerServices.ModuleInitializer]
    public static void InitModule() => throw new System.Exception();
#endif

    public static int Main() => Game.Play(6);

    public static long Wide() => Runtime.Combine(7, 0x123456789ABCDEF0L, -3) + 5;

    public static long Live() => Runtime.LongId(0x1122334455667788L) + Runtime.LongId(3);

    public static int Mutual() => Runtime.Even(10) ? 1 : 0;

    public static int VoidCalls()
    {
        Runtime.Ignore(7);
        return 9;
    }

    public static int Deep() => Runtime.Sum(10000);

    public static int Generic() => Runtime.Identity(1);

    public static int StaticInit() => Initialized.Read();

    public static void Dead() => throw new System.Exception();

    public static int Protected()
    {
        try
        {
            return Runtime.Answer();
        }
        catch (System.Exception)
        {
            return -1;
        }
    }
}
