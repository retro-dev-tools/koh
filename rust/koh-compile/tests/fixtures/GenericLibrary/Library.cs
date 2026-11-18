public static class Library
{
    public static System.Collections.Generic.IEnumerable<T> QueryForward<T>(
        System.Collections.Generic.IEnumerable<T> source
    ) => System.Linq.Enumerable.Where(source, _ => true);

    static T Identity<T>(T value) => value;

    public static T Forward<T>(T value) => Identity(value);

    public static T Constrained<T>(T value)
        where T : struct => Identity(value);

    public static T Create<T>()
        where T : new() => new T();

    public static float Unused() => 0.25f;
}

public sealed class RemoteMarker : IRemoteMarker
{
    public ushort Value;

    public int Read() => Value;
}

[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential,
    Pack = 1
)]
public struct RemotePayload
{
    public int Count;
    public long Wide;
}

public enum RemoteCode : long
{
    Value = 5000000001,
}

public interface IRemoteMarker
{
    int Read();
}

public delegate ushort RemoteReader(ushort value);

public sealed class ForeignCallbackBox<T>
{
    public System.Func<T, T> Callback;

    public ForeignCallbackBox(System.Func<T, T> callback) => Callback = callback;

    public T Read(T value) => Callback(value);

    public U Select<U>(T value, System.Func<T, U> selector) => selector(value);

    public System.Func<T, T> Capture(T value) => ignored => value;
}

public delegate T ForeignReferenceCallback<T>(T value)
    where T : class;

public delegate T ForeignFactory<out T>();

public sealed class ForeignConstrainedBox<T>
    where T : class
{
    public ForeignReferenceCallback<T> Callback;

    public ForeignConstrainedBox(ForeignReferenceCallback<T> callback) => Callback = callback;

    public T Read(T value) => Callback(value);
}

public class RemoteGenericBase
{
    public int Calls;

    public virtual T Forward<T>(T value)
    {
        Calls++;
        return value;
    }
}

public sealed class RemoteCell<T>
{
    public byte Head;
    public T Value;
    public ushort Tail;

    public RemoteCell(T value)
    {
        Head = 34;
        Value = value;
        Tail = 30601;
    }

    public T Read() => Value;
}

public class RemoteBase<T>
{
    public T Value;

    public RemoteBase(T value)
    {
        Value = value;
    }

    public T Read() => Value;
}

public struct RemoteReadValue<T> : IRemoteMarker
{
    public T Payload;
    public int Calls;

    public int Read()
    {
        Calls++;
        return Calls + 70;
    }
}

public class RemoteDispatchCell<T> : IRemoteMarker
{
    public T Payload = default!;
    public int Calls;

    public int Read()
    {
        Calls++;
        return Calls + 70;
    }

    public virtual T ReadValue() => Payload;
}

public interface IRemoteValue<T>
{
    T Read();
}

public struct RemoteInterfaceValue<T> : IRemoteValue<T>
{
    public T Value;
    public int Calls;

    T IRemoteValue<T>.Read()
    {
        Calls++;
        return Value;
    }
}

public class RemoteInterfaceCell<T> : IRemoteValue<T>
{
    public T Value = default!;
    public int Calls;

    public T Read()
    {
        Calls++;
        return Value;
    }
}

public interface IRemoteGenericRead<T>
{
    T Read<U>(U value);
}

public class RemoteGenericReference<T> : IRemoteGenericRead<T>
{
    public T Value = default!;
    public int Calls;

    public virtual U Forward<U>(U value)
    {
        Calls += 7;
        return value;
    }

    public virtual T Read<U>(U value)
    {
        Calls += 11;
        return Value;
    }
}
