public static class Library
{
    static T Identity<T>(T value) => value;

    public static T Forward<T>(T value) => Identity(value);

    public static T Constrained<T>(T value)
        where T : struct => Identity(value);

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
