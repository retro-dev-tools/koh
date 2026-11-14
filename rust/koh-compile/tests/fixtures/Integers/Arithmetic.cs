public static class Arithmetic
{
    public static int Sum()
    {
        int result = 0;
        for (int i = 1; i <= 10; i++)
            result += i;
        return result;
    }

    public static int Choose(int value) => value < 0 ? 7 : 9;

    public static int Wrap(int value) => unchecked(value + 1);

    public static long Wide(long value) => (value << 37) ^ (value >> 5);

    public static int Truncate(int value) => unchecked((sbyte)value);

    public static long Extend(int value) => value;

    public static int Small(sbyte value) => value;
}
