using System.Runtime.InteropServices;

public interface IValue<T>
{
    T Read();
}

public interface IGenericValue<T>
{
    T Read<U>(U value);
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Payload
{
    public byte Tag;
    public ushort Value;
}

public struct Cell<T> : IValue<T>, IGenericValue<T>
{
    public T Value;
    public int Calls;

    public T Read()
    {
        Calls++;
        return Value;
    }

    public T Read<U>(U value)
    {
        Calls++;
        return Value;
    }
}

public struct ExplicitCell<T> : IValue<T>, IGenericValue<T>
{
    public T Value;
    public int Calls;

    public T Read() => default;

    T IValue<T>.Read()
    {
        Calls++;
        return Value;
    }

    T IGenericValue<T>.Read<U>(U value)
    {
        Calls++;
        return Value;
    }
}

public struct DualCell : IValue<byte>, IValue<sbyte>
{
    public int ByteCalls;
    public int SignedCalls;

    byte IValue<byte>.Read()
    {
        ByteCalls++;
        return 245;
    }

    sbyte IValue<sbyte>.Read()
    {
        SignedCalls++;
        return -7;
    }
}

public static class Program
{
    public static ulong OtherOwner() => new ExplicitCell<ulong>().Read();

    private static T Read<TReceiver, T>(ref TReceiver receiver)
        where TReceiver : struct, IValue<T> => receiver.Read();

    private static T Generic<TReceiver, T, U>(ref TReceiver receiver, U argument)
        where TReceiver : struct, IGenericValue<T> => receiver.Read(argument);

    private static T Remote<TReceiver, T>(ref TReceiver receiver)
        where TReceiver : struct, IRemoteValue<T> => receiver.Read();

    public static int Main()
    {
        var small = new Cell<byte> { Value = 245 };
        var wide = new Cell<ulong> { Value = 0xFEDCBA9876543210UL };
        var dual = new DualCell();
        var packed = new ExplicitCell<Payload>
        {
            Value = new Payload { Tag = 17, Value = 60000 },
        };
        var remote = new RemoteInterfaceValue<Payload>
        {
            Value = new Payload { Tag = 19, Value = 50000 },
        };
        byte a = Read<Cell<byte>, byte>(ref small);
        ulong b = Read<Cell<ulong>, ulong>(ref wide);
        byte c = Read<DualCell, byte>(ref dual);
        sbyte d = Read<DualCell, sbyte>(ref dual);
        Payload e = Read<ExplicitCell<Payload>, Payload>(ref packed);
        Payload f = Generic<ExplicitCell<Payload>, Payload, ulong>(
            ref packed,
            0xFEDCBA9876543210UL
        );
        ulong g = Generic<Cell<ulong>, ulong, Payload>(ref wide, e);
        Payload h = Remote<RemoteInterfaceValue<Payload>, Payload>(ref remote);
        e.Tag = 99;
        return
            a == 245
            && b == 0xFEDCBA9876543210UL
            && c == 245
            && d == -7
            && small.Calls == 1
            && wide.Calls == 2
            && dual.ByteCalls == 1
            && dual.SignedCalls == 1
            && packed.Calls == 2
            && packed.Value.Tag == 17
            && packed.Value.Value == 60000
            && f.Tag == 17
            && f.Value == 60000
            && e.Value == 60000
            && g == 0xFEDCBA9876543210UL
            && h.Tag == 19
            && h.Value == 50000
            && remote.Calls == 1
            ? 42
            : 0;
    }
}
