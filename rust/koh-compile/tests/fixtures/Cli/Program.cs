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

    public static void UnsupportedThrow() => throw null!;

    public static int Switches()
    {
        int sum = 0;
        for (int i = -1; i <= 9; i++)
            sum = unchecked(sum * 31 + Select(i));
        return sum ^ Select(65536) ^ Select(int.MinValue) ^ Select(int.MaxValue);
    }

    private static int Select(int value)
    {
        switch (value)
        {
            case 0:
                return 17;
            case 1:
                return -23;
            case 2:
            case 3:
                return 91;
            case 4:
                return -1000;
            case 5:
                return 65536;
            case 6:
                return 12345;
            case 7:
                return -6789;
            default:
                return 42;
        }
    }
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

public static class BankedAbi
{
    private struct Pair
    {
        public byte Tag;
        public long Value;
    }

    public static int Run()
    {
        int value = 7;
        Pair pair = Make(-0x200000000L, 4);
        long result = Consume(pair, ref value);
        string text = Text();
        return
            result == -0x1FFFEDBCL
            && value == 16
            && Narrow(250, -300) == -50
            && text.Length == 3
            && text[1] == '\uD83D'
            ? 42
            : -1;
    }

    private static Pair Make(long value, int shift) =>
        new Pair { Tag = 9, Value = (value >> shift) + 0x1234 };

    private static long Consume(Pair pair, ref int value)
    {
        Add(ref value, pair.Tag);
        return pair.Value + value;
    }

    private static void Add(ref int value, byte amount) => value += amount;

    private static int Narrow(byte left, short right) => left + right;

    private static string Text() => "A\uD83D\uDE80";
}

#if AMBIGUOUS
public static class Alternative
{
    public static int Main() => 7;
}
#endif
