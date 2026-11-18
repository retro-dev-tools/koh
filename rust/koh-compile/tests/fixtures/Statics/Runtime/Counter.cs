public static class Counter
{
    public static int Value;

    public static int Read() => Value;
}

public static class Unreachable
{
    public static object Dead = new object();
}
