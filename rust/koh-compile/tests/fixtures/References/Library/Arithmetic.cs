namespace Game.Runtime;

public static class Arithmetic
{
    public static int Counter;

    public static int Add(int left, int right) => left + right;

    public static long Add(long left, int right) => left + right;

    public static T Identity<T>(T value) => value;

    public static Payload Pass(Payload value) => value;

    public static Holder<int> Wrapped(Holder<int> value) => value;
}

public sealed class Payload;

public sealed class Holder<T>
{
    public T Value = default!;

    public T Echo(T value) => value;
}

public sealed class Extra<T>;

public static class Outer
{
    public sealed class Inner;
}
