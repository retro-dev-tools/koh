using System.Runtime.InteropServices;

public static class Program
{
    public static int UnsupportedGenericReferenceMethod() =>
        new GenericVirtualReference<byte>().Forward(42);

    public static int InheritedClosedBody()
    {
        var small = new MappedCell<byte> { Payload = 245 };
        var wide = new MappedCell<ulong> { Payload = 0xFEDCBA9876543210UL, Calls = 10 };
        IClosedRead left = small;
        IClosedRead right = wide;
        int a = left.Read();
        int b = right.Read();
        var fixedCell = new FixedMappedCell { Payload = 0x123456789ABCDEF0UL };
        IClosedRead fixedRead = fixedCell;
        int c = fixedRead.Read();
        return
            a == 72
            && b == 82
            && small.Calls == 2
            && wide.Calls == 12
            && small.Payload == 245
            && wide.Payload == 0xFEDCBA9876543210UL
            && c == 72
            && fixedCell.Calls == 2
            && fixedCell.Payload == 0x123456789ABCDEF0UL
            ? 42
            : 0;
    }

    public static int InheritedRemoteMembers()
    {
        var cell = new LocalDerived<Payload>(new Payload { Tag = 17, Value = 60000 });
        return cell.Read().Tag == 17 && cell.Read().Value == 60000 && cell.Tag == 42 ? 42 : 0;
    }

    public static int InheritedMembers()
    {
        var cell = new DerivedCell<byte>(245, 17);
        return
            cell.Read() == 245
            && cell.Value == 245
            && cell.Tail == 17
            && cell.Read(42) == 42
            && cell.Echo(0xFEDCBA9876543210UL) == 0xFEDCBA9876543210UL
            && ReadInherited(cell, (byte)42) == 42
            ? 42
            : 0;
    }

    static T ReadInherited<T>(object instance, T value)
    {
        var cell = (DerivedCell<T>)instance;
        cell.Value = value;
        return cell.Read();
    }

    public static int Inheritance()
    {
        var small = new DerivedCell<byte>(245, 17);
        var wide = new DerivedCell<ulong>(0xFEDCBA9876543210UL, 60000);
        BaseCell<byte> parent = small;
        parent.Value = 42;
        var nested = new NestedDerived<byte, ushort>(
            new Pair<ushort>
            {
                Head = 7,
                Value = 60000,
                Tail = 30600,
            },
            245
        );
        var remote = new LocalDerived<Payload>(new Payload { Tag = 17, Value = 60000 });
        var fixedWide = new FixedDerived(0x123456789ABCDEF0UL);
        return
            small.Read() == 42
            && small.Tail == 17
            && wide.Read() == 0xFEDCBA9876543210UL
            && wide.Tail == 60000
            && nested.Read().Head == 7
            && nested.Read().Value == 60000
            && nested.Read().Tail == 30600
            && nested.Tail == 30600
            && nested.Extra == 245
            && remote.Read().Tag == 17
            && remote.Read().Value == 60000
            && remote.Tag == 42
            && fixedWide.Read() == 0x123456789ABCDEF0UL
            && fixedWide.Tail == 17
            ? 42
            : 0;
    }

    static T Copy<T>(T value) => value;

    static RemoteCell<T> Make<T>(T value) => new RemoteCell<T>(value);

    public static int TypeOnly() => Cell<RemotePayload>.Constant();

    public static int CallbackOwned()
    {
        var cell = new Cell<byte>(245);
        Getter<byte> read = cell.Read;
        cell.Value = 42;
        var packed = new Cell<Pair<byte>>(
            new Pair<byte>
            {
                Head = 17,
                Value = 245,
                Tail = 30600,
            }
        );
        Getter<Pair<byte>> readPacked = packed.Read;
        var result = readPacked();
        var remote = Make(new Payload { Tag = 7, Value = 60000 });
        Getter<Payload> readRemote = remote.Read;
        return
            read() == 42
            && result.Head == 17
            && result.Value == 245
            && result.Tail == 30600
            && readRemote().Tag == 7
            && readRemote().Value == 60000
            ? 42
            : 0;
    }

    public static int InvalidExplicit() => ExplicitValue<byte>.Constant();

    public static int Arrays()
    {
        byte[] vector = new byte[2];
        vector[1] = 245;
        var rectangle = new RemotePayload[2, 2];
        rectangle[1, 0] = new RemotePayload { Count = -70000, Wide = -5000000001 };
        var vectorCell = new Cell<byte[]>(vector);
        var rectangleCell = new Cell<RemotePayload[,]>(rectangle);
        Storage<byte[]>.Value = vectorCell.Read();
        Storage<RemotePayload[,]>.Value = rectangleCell.Read();
        Storage<byte[]>.Read()[0] = 42;
        return
            vector[0] == 42
            && vectorCell.Read()[1] == 245
            && rectangleCell.Read() == rectangle
            && Storage<RemotePayload[,]>.Read()[1, 0].Count == -70000
            && Storage<RemotePayload[,]>.Read()[1, 0].Wide == -5000000001
            && Storage<byte[]>.Zero == null
            && Storage<RemotePayload[,]>.Zero == null
            && Counter.Total == 2
            ? 42
            : 0;
    }

    public static int InterfaceDispatch()
    {
        var small = new InterfaceCell<byte> { Payload = 245 };
        var wide = new InterfaceCell<ulong> { Payload = 0xFEDCBA9876543210UL, Calls = 10 };
        IClosedRead left = small;
        IClosedRead right = wide;
        int first = left.Read();
        int second = right.Read();
        int third = left.Read();
        var explicitCell = new ExplicitInterfaceCell<Payload>
        {
            Payload = new Payload { Tag = 17, Value = 60000 },
        };
        IClosedRead explicitRead = explicitCell;
        int fourth = explicitRead.Read();
        int fifth = explicitRead.Read();
        var overridden = new OverrideCell<ulong> { Payload = 0xFEDCBA9876543210UL, Calls = 4 };
        VirtualCell<ulong> virtualRead = overridden;
        int sixth = virtualRead.Read();
        IClosedRead inheritedRead = overridden;
        int seventh = inheritedRead.Read();
        var plain = new VirtualCell<byte> { Payload = 245 };
        int eighth = plain.Read();
        VirtualCell<Payload> aggregate = new OverrideCell<Payload>
        {
            Payload = new Payload { Tag = 7, Value = 60000 },
        };
        Payload copied = aggregate.ReadValue();
        copied.Value = 17;
        Getter<Payload> capturedRead = aggregate.ReadValue;
        Payload capturedCopy = capturedRead();
        aggregate.Payload.Value = 30600;
        Payload changedCopy = capturedRead();
        VirtualCell<ulong> fixedRead = new FixedOverride { Payload = 0x123456789ABCDEF0UL };
        int ninth = fixedRead.Read();
        var remote = new RemoteDispatchCell<Payload>
        {
            Payload = new Payload { Tag = 14, Value = 60000 },
            Calls = 7,
        };
        IRemoteMarker remoteRead = remote;
        int tenth = remoteRead.Read();
        Payload remoteCopy = remote.ReadValue();
        remoteCopy.Value = 17;
        int thirteenth = ReadGeneric(ref small);
        var defaultCell = new DefaultReadCell<byte> { Payload = 245 };
        IClosedDefault defaultRead = defaultCell;
        int eleventh = defaultRead.Read();
        int twelfth = defaultRead.Read();
        return
            first == 42
            && second == 52
            && third == 43
            && small.Calls == 3
            && thirteenth == 44
            && wide.Calls == 11
            && small.Payload == 245
            && wide.Payload == 0xFEDCBA9876543210UL
            && fourth == 103
            && fifth == 106
            && explicitCell.Read() == -1
            && explicitCell.Calls == 6
            && explicitCell.Payload.Tag == 17
            && explicitCell.Payload.Value == 60000
            && sixth == 76
            && seventh == 78
            && overridden.Calls == 8
            && overridden.Payload == 0xFEDCBA9876543210UL
            && eighth == 11
            && plain.Calls == 1
            && plain.Payload == 245
            && copied.Tag == 7
            && copied.Value == 17
            && aggregate.Payload.Value == 30600
            && aggregate.Calls == 15
            && capturedCopy.Tag == 7
            && capturedCopy.Value == 60000
            && changedCopy.Tag == 7
            && changedCopy.Value == 30600
            && ninth == 93
            && fixedRead.Calls == 3
            && fixedRead.Payload == 0x123456789ABCDEF0UL
            && tenth == 78
            && remote.Calls == 8
            && remote.Payload.Tag == 14
            && remote.Payload.Value == 60000
            && remoteCopy.Tag == 14
            && remoteCopy.Value == 17
            && eleventh == 6
            && twelfth == 7
            && defaultCell.Calls == 2
            && defaultCell.Payload == 245
            ? 42
            : 0;
    }

    static int ReadGeneric<T>(ref T value)
        where T : IClosedRead => value.Read();

    static int ReadRemoteGeneric<T>(ref T value)
        where T : IRemoteMarker => value.Read();

    static U ForwardGeneric<T, U>(ref T receiver, U value)
        where T : IClosedForward => receiver.Forward(value);

    public static int ConstrainedDispatch()
    {
        var value = new InterfaceValue<byte> { Payload = 245 };
        int first = ReadGeneric(ref value);
        int second = ReadGeneric(ref value);
        var wide = new InterfaceValue<ulong> { Payload = 0xFEDCBA9876543210UL, Calls = 10 };
        int third = ReadGeneric(ref wide);
        var explicitValue = new ExplicitInterfaceValue<byte> { Payload = 17 };
        int fourth = ReadGeneric(ref explicitValue);
        int decoy = explicitValue.Read();
        var forwarded = ForwardGeneric(ref explicitValue, new Payload { Tag = 34, Value = 60000 });
        ulong forwardedWide = ForwardGeneric(ref wide, 0xFEDCBA9876543210UL);
        var remote = new RemoteReadValue<Payload>
        {
            Payload = new Payload { Tag = 7, Value = 60000 },
            Calls = 7,
        };
        int fifth = ReadRemoteGeneric(ref remote);
        return
            first == 42
            && second == 43
            && value.Calls == 2
            && value.Payload == 245
            && third == 52
            && wide.Calls == 14
            && wide.Payload == 0xFEDCBA9876543210UL
            && fourth == 101
            && explicitValue.Calls == 11
            && explicitValue.Payload == 17
            && decoy == 201
            && forwarded.Tag == 34
            && forwarded.Value == 60000
            && forwardedWide == 0xFEDCBA9876543210UL
            && fifth == 78
            && remote.Calls == 8
            && remote.Payload.Tag == 7
            && remote.Payload.Value == 60000
            ? 42
            : 0;
    }

    static Pair<byte> EchoPair(Pair<byte> value) => value;

    public static int CallbackWarm()
    {
        var wide = new Pair<ulong>(0xFEDCBA9876543210UL);
        PairHandler<Pair<byte>> handler = EchoPair;
        var result = handler(
            new Pair<byte>
            {
                Head = 17,
                Value = 245,
                Tail = 30600,
            }
        );
        return
            wide.Read() == 0xFEDCBA9876543210UL
            && result.Head == 17
            && result.Value == 245
            && result.Tail == 30600
            ? 42
            : 0;
    }

    public static int CallbackCold()
    {
        PairHandler<Pair<byte>> handler = EchoPair;
        var result = handler(
            new Pair<byte>
            {
                Head = 17,
                Value = 245,
                Tail = 30600,
            }
        );
        return result.Head == 17 && result.Value == 245 && result.Tail == 30600 ? 42 : 0;
    }

    public static int Construction()
    {
        var reference = Factory<DefaultCell<byte>>.Create();
        var value = Factory<Pair<Payload>>.Create();
        return
            reference != null
            && reference.Value == 0
            && value.Head == 0
            && value.Value.Value == 0
            && value.Tail == 0
            ? 42
            : 0;
    }

    public static int References()
    {
        var cell = new Cell<byte>(245);
        var pair = new Pair<Cell<byte>>
        {
            Head = 17,
            Value = cell,
            Tail = 30600,
        };
        var copy = Copy(pair);
        object erased = cell;
        Storage<Cell<byte>>.Value = cell;
        Storage<Pair<Cell<byte>>>.Value = copy;
        copy.Value.Value = 42;
        return
            pair.Value == cell
            && erased is Cell<byte>
            && !(erased is Cell<ulong>)
            && !(erased is Cell<sbyte>)
            && pair.Value.Read() == 42
            && Storage<Cell<byte>>.Read() == cell
            && Storage<Pair<Cell<byte>>>.Read().Value == cell
            && Storage<Pair<Cell<byte>>>.Read().Head == 17
            && Storage<Pair<Cell<byte>>>.Read().Tail == 30600
            && Storage<Cell<byte>>.Zero == null
            && Storage<Pair<Cell<byte>>>.Zero.Value == null
            && Counter.Total == 2
            ? 42
            : 0;
    }

    public static int Values()
    {
        var narrow = new Pair<byte>
        {
            Head = 17,
            Value = 245,
            Tail = 30600,
        };
        var wide = new Pair<ulong>(0xFEDCBA9876543210UL);
        var packed = new Pair<Payload>
        {
            Head = 51,
            Value = new Payload { Tag = 7, Value = 60000 },
            Tail = 30602,
        };
        var alias = Copy(wide);
        alias.Value = 5000000001;
        return
            narrow.Head == 17
            && narrow.Value == 245
            && narrow.Tail == 30600
            && wide.Head == 34
            && wide.Value == 0xFEDCBA9876543210UL
            && wide.Read() == 0xFEDCBA9876543210UL
            && wide.Tail == 30601
            && alias.Value == 5000000001
            && packed.Head == 51
            && packed.Value.Tag == 7
            && packed.Value.Value == 60000
            && packed.Tail == 30602
            ? 42
            : 0;
    }

    public static int Objects()
    {
        var narrow = new Cell<byte>(245);
        var wide = new Cell<ulong>(0xFEDCBA9876543210UL);
        var local = Make(new Payload { Tag = 7, Value = 60000 });
        var remote = new Cell<RemotePayload>(
            new RemotePayload { Count = -70000, Wide = -5000000001 }
        );
        var alias = Copy(wide);
        alias.Value = 5000000001;
        return
            narrow.Read() == 245
            && narrow.Head == 17
            && narrow.Tail == 30600
            && wide.Read() == 5000000001
            && alias == wide
            && local.Read().Tag == 7
            && local.Read().Value == 60000
            && local.Head == 34
            && local.Tail == 30601
            && remote.Read().Count == -70000
            && remote.Read().Wide == -5000000001
            && remote.Echo<ushort>(60000) == 60000
            ? 42
            : 0;
    }

    public static int Statics()
    {
        Storage<byte>.Value = 245;
        Storage<ulong>.Value = 0xFEDCBA9876543210UL;
        Storage<Payload>.Value = new Payload { Tag = 7, Value = 60000 };
        Storage<RemotePayload>.Value = new RemotePayload { Count = -70000, Wide = -5000000001 };
        return
            Storage<byte>.Read() == 245
            && Storage<ulong>.Read() == 0xFEDCBA9876543210UL
            && Storage<Payload>.Read().Tag == 7
            && Storage<Payload>.Read().Value == 60000
            && Storage<RemotePayload>.Read().Count == -70000
            && Storage<RemotePayload>.Read().Wide == -5000000001
            && Storage<byte>.Runs == 1
            && Storage<ulong>.Runs == 1
            && Storage<Payload>.Runs == 1
            && Storage<RemotePayload>.Runs == 1
            && Storage<byte>.Zero == 0
            && Storage<ulong>.Zero == 0
            && Storage<Payload>.Zero.Value == 0
            && Storage<RemotePayload>.Zero.Wide == 0
            && Counter.Total == 4
            ? 42
            : 0;
    }
}

class BaseCell<T>
{
    public T Value;

    public BaseCell(T value)
    {
        Value = value;
    }

    public T Read() => Value;

    public byte Read(byte marker) => marker;

    public U Echo<U>(U value) => value;
}

class DerivedCell<T> : BaseCell<T>
{
    public ushort Tail;

    public DerivedCell(T value, ushort tail)
        : base(value)
    {
        Tail = tail;
    }
}

class NestedDerived<A, B> : DerivedCell<Pair<B>>
{
    public A Extra;

    public NestedDerived(Pair<B> value, A extra)
        : base(value, 30600)
    {
        Extra = extra;
    }
}

class FixedDerived : DerivedCell<ulong>
{
    public FixedDerived(ulong value)
        : base(value, 17) { }
}

class LocalDerived<T> : RemoteBase<T>
{
    public byte Tag;

    public LocalDerived(T value)
        : base(value)
    {
        Tag = 42;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Pair<T>
{
    public byte Head;
    public T Value;
    public ushort Tail;

    public Pair(T value)
    {
        Head = 34;
        Value = value;
        Tail = 30601;
    }

    public T Read() => Value;
}

public sealed class DefaultCell<T>
{
    public T Value;
}

public delegate T PairHandler<T>(T value);

public delegate T Getter<T>();

public interface IClosedRead
{
    int Read();
}

public sealed class InterfaceCell<T> : IClosedRead
{
    public T Payload;
    public int Calls;

    public int Read()
    {
        Calls++;
        return Calls + 41;
    }
}

public sealed class ExplicitInterfaceCell<T> : IClosedRead
{
    public T Payload;
    public int Calls;

    int IClosedRead.Read()
    {
        Calls += 3;
        return Calls + 100;
    }

    public int Read() => -1;
}

public class VirtualCell<T> : IClosedRead
{
    public T Payload;
    public int Calls;

    public virtual int Read()
    {
        Calls++;
        return Calls + 10;
    }

    public virtual T ReadValue()
    {
        Calls++;
        return Payload;
    }
}

public class OverrideCell<T> : VirtualCell<T>
{
    public override int Read()
    {
        Calls += 2;
        return Calls + 70;
    }

    public override T ReadValue()
    {
        Calls += 5;
        return Payload;
    }
}

public sealed class FixedOverride : OverrideCell<ulong>
{
    public override int Read()
    {
        Calls += 3;
        return Calls + 90;
    }
}

public sealed class MappedCell<T> : OverrideCell<T>, IClosedRead
{
    int IClosedRead.Read()
    {
        Calls += 20;
        return Calls + 200;
    }
}

public sealed class FixedMappedCell : OverrideCell<ulong>, IClosedRead
{
    int IClosedRead.Read()
    {
        Calls += 30;
        return Calls + 300;
    }
}

public class GenericVirtualReference<T>
{
    public virtual U Forward<U>(U value) => value;
}

public interface IClosedDefault
{
    int Next();

    int Read() => Next() + 5;
}

public sealed class DefaultReadCell<T> : IClosedDefault
{
    public T Payload;
    public int Calls;

    public int Next() => ++Calls;
}

public struct InterfaceValue<T> : IClosedRead, IClosedForward
{
    public T Payload;
    public int Calls;

    public int Read()
    {
        Calls++;
        return Calls + 41;
    }

    public U Forward<U>(U value)
    {
        Calls += 3;
        return value;
    }
}

public struct ExplicitInterfaceValue<T> : IClosedRead, IClosedForward
{
    public T Payload;
    public int Calls;

    int IClosedRead.Read()
    {
        Calls++;
        return Calls + 100;
    }

    public int Read() => Calls + 200;

    U IClosedForward.Forward<U>(U value)
    {
        Calls += 10;
        return value;
    }
}

public interface IClosedForward
{
    U Forward<U>(U value);
}

[StructLayout(LayoutKind.Explicit)]
public struct ExplicitValue<T>
{
    [FieldOffset(0)]
    public int Value;

    public static int Constant() => 42;
}

public static class Factory<T>
    where T : new()
{
    public static T Create() => new T();
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Payload
{
    public byte Tag;
    public ushort Value;
}

public sealed class Cell<T>
{
    public static int Constant() => 42;

    public byte Head;
    public T Value;
    public ushort Tail;

    public Cell(T value)
    {
        Head = 17;
        Value = value;
        Tail = 30600;
    }

    public T Read() => Value;

    public U Echo<U>(U value) => value;
}

public static class Counter
{
    public static int Total;
}

public static class Storage<T>
{
    public static T Value;
    public static int Runs;
    public static readonly T Zero;

    static Storage()
    {
        Counter.Total++;
        Runs++;
        Zero = default;
    }

    public static T Read() => Value;
}
