public interface IProducer<out T> { }

public interface IConsumer<in T> { }

public delegate T Producer<out T>();

public delegate void Consumer<in T>(T value);

public sealed class Payload { }

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

    private static int Probe<T>() => 42;

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
