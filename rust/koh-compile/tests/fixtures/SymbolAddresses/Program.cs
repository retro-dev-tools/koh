public static class Program
{
    public static byte Counter;
    public static byte Evidence;
    public static byte Zero;

    public static unsafe int First()
    {
        Helper();
        Counter = 1;
        return *(byte*)0xC020 | (*(byte*)0xC021 << 8) | (*(byte*)0xC022 << 16) | (Zero << 24);
    }

    public static unsafe int Second()
    {
        Helper();
        Counter = 1;
        return *(byte*)0xC040 | (*(byte*)0xC041 << 8) | (*(byte*)0xC042 << 16) | (Zero << 24);
    }

    private static void Helper()
    {
        Evidence = 2;
    }
}
