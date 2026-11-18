public static class Program
{
    public static unsafe void Main()
    {
        *(int*)0xC900 = Add(17, 25);
        while (true) { }
    }

    private static int Add(int left, int right) => left + right;
}
