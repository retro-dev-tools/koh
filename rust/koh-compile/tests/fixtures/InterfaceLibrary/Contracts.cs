namespace InterfaceLibrary;

public interface IRemote
{
    int Value(int value);
}

public class RemoteBase : IRemote
{
    public virtual int Value(int value) => value + 5;
}

public sealed class RemoteExplicit : IRemote
{
    int IRemote.Value(int value) => value + 7;
}

public interface IRemoteRoot
{
    int Value(int value) => value + 1;
}

public interface IRemoteDefault : IRemoteRoot
{
    int IRemoteRoot.Value(int value) => value + 7;
}

public sealed class RemoteDefault : IRemoteDefault { }
