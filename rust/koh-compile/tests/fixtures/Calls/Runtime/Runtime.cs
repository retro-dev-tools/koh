public static class Runtime
{
    public static int Sum(int n) => n <= 0 ? 0 : n + Sum(n - 1);

    public static int StackDepth(int n) => n <= 0 ? 0 : n - StackDepth(n - 1);

    public static int Answer() => 21;

    public static long Combine(int a, long b, int c) => b + a + c;

    public static long LongId(long value) => value;

    public static bool Even(int n) => n == 0 || Odd(n - 1);

    public static bool Odd(int n) => n != 0 && Even(n - 1);

    public static void Ignore(int value) { }

    public static T Identity<T>(T value) => value;
}

public static class Initialized
{
    static Initialized() => throw new System.Exception();

    public static int Read() => 1;
}
