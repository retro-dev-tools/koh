public static class Program
{
#if !NESTED
    [KohIntrinsic("halt")]
    public static int Main() => Sum(6) + 21;
#endif

    private static int Sum(int n) => n <= 0 ? 0 : n + Sum(n - 1);

    public static void Other() { }

    public static int Word() => unchecked((int)0x89ABCDEF);

    public static int NeedsArgument(int value) => value;

    public static long LongEntry() => 42;

    public static void Unsupported() => throw new System.Exception();
}

#if NESTED
namespace Demo
{
    public static class Outer
    {
        public static class Program
        {
            public static int Main() => 17;
        }
    }

    public static class Another
    {
        public static class Program
        {
            public static int Main() => 23;
        }
    }
}
#endif

public sealed class KohIntrinsicAttribute(string value) : System.Attribute
{
    public string Value { get; } = value;
}

#if AMBIGUOUS
public static class Alternative
{
    public static int Main() => 7;
}
#endif
