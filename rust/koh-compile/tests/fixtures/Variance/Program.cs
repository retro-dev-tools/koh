public interface IProducer<out T>
{
    T Get();
}

public interface IConsumer<in T>
{
    void Set(T value);
}

public interface IComplex<out T> : IProducer<T>
{
    IProducer<T[]> Nest();
    void Accept(IConsumer<T> value);
}

public interface IStatic<out T>
{
    static virtual T Get() => default;
}

public interface IRef<T>
{
    void Change(ref T value);
}

public delegate T Producer<out T>();

public delegate void Consumer<in T>(T value);

public sealed class Payload
{
    public int Value = 42;
}

public sealed class ProducerImpl : IProducer<Payload>
{
    public Payload Get() => new Payload();
}

public sealed class IntProducer : IProducer<int>
{
    public int Get() => 42;
}

public sealed class ConsumerImpl : IConsumer<Payload>
{
    public static int Observed;

    public void Set(Payload value) => Observed = value.Value;
}

public static class Program
{
    private static int observed;

    private static int Produce() => 42;

    private static void Consume(int value) => observed = value;

    public static int ProducerCall()
    {
        var callback = new Producer<int>(Produce);
        return callback();
    }

    public static int ConsumerCall()
    {
        var callback = new Consumer<int>(Consume);
        callback(42);
        return observed;
    }

    public static int ExactProducer()
    {
        IProducer<Payload> value = new ProducerImpl();
        return value.Get().Value;
    }

    public static int ExactIntProducer()
    {
        IProducer<int> value = new IntProducer();
        return value.Get();
    }

    public static int ExactConsumer()
    {
        IConsumer<Payload> value = new ConsumerImpl();
        value.Set(new Payload());
        return ConsumerImpl.Observed;
    }

    private static object Widen(IProducer<object> value) => value.Get();

    public static int CovariantConversion() => Widen(new ProducerImpl()) is null ? 0 : 42;

    private static bool VariantTest(object value) => value is IProducer<object>;

    public static int CovariantTypeTest() => VariantTest(new ProducerImpl()) ? 42 : 0;

    private static IProducer<object> VariantCast(object value) => (IProducer<object>)value;

    public static int CovariantCast() => VariantCast(new ProducerImpl()) is null ? 0 : 42;

    private static int Probe<T>() => 42;

    public static int ComplexShape() => Probe<IComplex<Payload>>();

    public static int StaticShape() => Probe<IStatic<Payload>>();

    public static int RefShape() => Probe<IRef<Payload>>();

    public static int IProducerShape() => Probe<IProducer<Payload>>();

    public static int IConsumerShape() => Probe<IConsumer<Payload>>();

    public static int ProducerShape() => Probe<Producer<Payload>>();

    public static int ConsumerShape() => Probe<Consumer<Payload>>();

    public static int Boxed()
    {
        object value = 42;
        return (int)value;
    }

    public static int Main()
    {
        System.Type[] types =
        {
            typeof(IProducer<Payload>),
            typeof(IConsumer<Payload>),
            typeof(Producer<Payload>),
            typeof(Consumer<Payload>),
        };
        return types.Length == 4 && Boxed() == 42 && ProducerCall() == 42 && ConsumerCall() == 42
            ? 0
            : 1;
    }
}
