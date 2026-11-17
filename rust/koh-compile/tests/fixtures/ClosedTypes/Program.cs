using System.Runtime.InteropServices;

public static class Program
{
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

    public static int InterfaceDispatch() => new InterfaceCell<byte>().Read();

    static int ReadGeneric<T>(ref T value)
        where T : IClosedRead => value.Read();

    public static int ConstrainedDispatch()
    {
        var value = new InterfaceValue<byte>();
        return ReadGeneric(ref value);
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
    public int Read() => 42;
}

public struct InterfaceValue<T> : IClosedRead
{
    public int Read() => 42;
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
