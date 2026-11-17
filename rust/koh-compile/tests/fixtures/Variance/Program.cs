public interface IProducer<out T> { }

public interface IConsumer<in T> { }

public sealed class Payload { }

public static class Program
{
    private static int Probe<T>() => 42;

    public static int IProducerShape() => Probe<IProducer<Payload>>();

    public static int IConsumerShape() => Probe<IConsumer<Payload>>();

    public static int Boxed()
    {
        object value = 42;
        return (int)value;
    }

    public static int Main()
    {
        System.Type[] types = { typeof(IProducer<Payload>), typeof(IConsumer<Payload>) };
        return types.Length == 2 && Boxed() == 42 ? 0 : 1;
    }
}
