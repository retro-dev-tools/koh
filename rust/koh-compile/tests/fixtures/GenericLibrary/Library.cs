public static class Library
{
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
