using Game.Runtime;

namespace Game.Bridge;

public static class Api
{
    public static int Compute(int value)
    {
        Arithmetic.Counter = value;
        return Arithmetic.Add(Arithmetic.Identity<int>(value), 7);
    }

    public static Payload Pass(Payload value) => Arithmetic.Pass(value);

    public static int Echo(Holder<int> holder) => holder.Echo(holder.Value);

    public static Outer.Inner Nested(Outer.Inner value) => value;

    public static Holder<int> Wrapped(Holder<int> value) => Arithmetic.Wrapped(value);

    public static Holder<int> GenericWrap(Holder<int> value) =>
        Arithmetic.Identity<Holder<int>>(value);
}
