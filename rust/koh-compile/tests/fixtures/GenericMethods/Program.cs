public static class Program
{
    static T Identity<T>(T value) => value;

    static T Wrap<T>(T value) => Identity<T>(value);

    static T Recursive<T>(T value, int count) =>
        count == 0 ? value : Recursive<T>(value, count - 1);

    static T Local<T>(T value)
    {
        T copy = value;
        return Wrap(copy);
    }

    static T Make<T>(T value)
    {
        T[] items = new T[2];
        items[1] = value;
        return items[1];
    }

    static void Swap<T>(ref T left, ref T right)
    {
        T copy = left;
        left = right;
        right = copy;
    }

    static T Zero<T>() => default;

    static float Unused<T>(T value) => 0.25f;

    static int Expand<T>() => Expand<T[]>();

    static T Constrained<T>(T value)
        where T : struct => Library.Constrained(value);

    static T ReferenceOnly<T>(T value)
        where T : class => value;

    static T Defaultable<T>(T value)
        where T : new() => value;

    static T BaseOnly<T>(T value)
        where T : ConstraintBase => value;

    static T InterfaceOnly<T>(T value)
        where T : IMarker => value;

    static T Related<T, U>(T value)
        where T : U => value;

    static T Combined<T>(T value)
        where T : ConstraintBase, IMarker => value;

    static T EnumOnly<T>(T value)
        where T : System.Enum => value;

    static int ConstructorType<T>()
        where T : new() => 42;

    public static int ConstructorEligibility() => ConstructorType<Marker>();

    static T Create<T>()
        where T : new() => new T();

    public static T CreateInstance<T>() => default;

    public static int ActivatorProbe() => Create<Marker>() == null ? 42 : 0;

    public static int InvalidActivation() => System.Activator.CreateInstance<string>().Length;

    public static int GenericConstruction()
    {
        ConstructedValue.Calls = 0;
        Marker first = Create<Marker>();
        Marker second = Create<Marker>();
        ConstructedValue value = Create<ConstructedValue>();
        Payload zero = Create<Payload>();
        object plain = Create<object>();
        object otherPlain = Create<object>();
        ConstructedValue remoteValue = Library.Create<ConstructedValue>();
        RemoteMarker remoteMarker = Library.Create<RemoteMarker>();
        first.Value = 17;
        return
            first != second
            && second.Value == 0
            && value.Value == 42
            && zero.Tag == 0
            && zero.Value == 0
            && plain != null
            && plain != otherPlain
            && remoteValue.Value == 42
            && ConstructedValue.Calls == 2
            && remoteMarker.Value == 0
            && Create<int>() == 0
            && Create<ulong>() == 0
            ? 42
            : 0;
    }

    public static int Expanding() => Expand<int>();

    static int Advance<T>(ref T value, int delta)
        where T : IMutableCounter => value.Next(delta);

    public static int ConstrainedValues()
    {
        var value = new MutableValue { Value = 1 };
        int first = Advance(ref value, 41);
        var copy = value;
        int second = Advance(ref copy, 1);
        var explicitValue = new ExplicitMutableValue { Value = 20 };
        int third = Advance(ref explicitValue, 22);
        return
            first == 42
            && second == 43
            && value.Value == 42
            && copy.Value == 43
            && third == 42
            && explicitValue.Value == 42
            ? 42
            : 0;
    }

    public static int ConstrainedReferences()
    {
        var value = new MutableClass { Value = 1 };
        var alias = value;
        int first = Advance(ref value, 41);
        IMutableCounter view = value;
        int second = Advance(ref view, 1);
        return first == 42 && second == 43 && alias.Value == 43 ? 42 : 0;
    }

    public static int ConstrainedNull()
    {
        MutableClass value = null;
        return Advance(ref value, 42);
    }

    static U ConstrainedForward<T, U>(ref T receiver, U value)
        where T : IGenericForward => receiver.Forward(value);

    public static int ConstrainedGenerics()
    {
        var value = new GenericForwardValue();
        var explicitValue = new ExplicitGenericForwardValue();
        var reference = new GenericDerived();
        var payload = new Payload { Tag = 7, Value = 60000 };
        Payload first = ConstrainedForward(ref value, payload);
        Payload second = ConstrainedForward(ref explicitValue, payload);
        Payload third = ConstrainedForward(ref reference, payload);
        return
            first.Tag == 7
            && first.Value == 60000
            && second.Value == 60000
            && third.Value == 60000
            && value.Calls == 1
            && explicitValue.Calls == 1
            && reference.Value == 100
            ? 42
            : 0;
    }

    public static int GenericNullCall()
    {
        GenericBase value = null;
        return value.Forward<int>(42);
    }

    public static int GenericNullGroup()
    {
        IGenericForward value = null;
        Reader callback = value.Forward<int>;
        return callback(42);
    }

    public static int GenericBaseCalls()
    {
        var value = new GenericDerived();
        long wide = value.Direct<long>(-5000000001);
        Reader callback = value.BaseGroup();
        int result = callback(42);
        return wide == -5000000001 && result == 42 && value.Value == 2 ? 42 : 0;
    }

    public static int GenericVirtualCall()
    {
        GenericBase value = new GenericDerived();
        long wide = value.Forward<long>(-5000000001);
        ushort narrow = value.Forward<ushort>(60000);
        return wide == -5000000001 && narrow == 60000 && value.Value == 200 ? 42 : 0;
    }

    public static int GenericVirtualGroup()
    {
        GenericBase value = new GenericDerived();
        GenericBase original = value;
        Reader callback = value.Forward<int>;
        value = new GenericBase();
        return callback(42) == 42 && original.Value == 100 && value.Value == 0 ? 42 : 0;
    }

    public static int GenericInterfaces()
    {
        var derived = new GenericDerived();
        IGenericForward implicitView = derived;
        long wide = implicitView.Forward<long>(-5000000001);
        Reader callback = implicitView.Forward<int>;
        var explicitValue = new GenericExplicit();
        IGenericForward explicitView = explicitValue;
        ushort narrow = explicitView.Forward<ushort>(60000);
        IGenericDefault defaultView = new GenericDefault();
        Payload payload = defaultView.Forward(new Payload { Tag = 7, Value = 60000 });
        return
            wide == -5000000001
            && callback(42) == 42
            && derived.Value == 200
            && narrow == 60000
            && explicitValue.Calls == 1
            && payload.Tag == 7
            && payload.Value == 60000
            ? 42
            : 0;
    }

    public static int GenericReferences()
    {
        var plain = new RemoteGenericBase();
        Payload first = plain.Forward(new Payload { Tag = 7, Value = 60000 });
        RemoteGenericBase derived = new GenericForeignDerived();
        Payload second = derived.Forward(first);
        PayloadReader callback = derived.Forward<Payload>;
        Payload third = callback(second);
        return first.Tag == 7 && third.Value == 60000 && plain.Calls == 1 && derived.Calls == 20
            ? 42
            : 0;
    }

    public static int GenericDelegates()
    {
        Reader first = Identity<int>;
        RemoteReader second = Library.Forward<ushort>;
        RemoteReader third = Identity<ushort>;
        var marker = new GenericReceiver { Value = 17 };
        Reader bound = marker.Forward<int>;
        PayloadReader packed = Library.Forward<Payload>;
        Payload payload = packed(new Payload { Tag = 7, Value = 60000 });
        return
            first(42) == 42
            && second(60000) == 60000
            && third(50000) == 50000
            && bound(1000) == 1000
            && marker.Value == 18
            && payload.Tag == 7
            && payload.Value == 60000
            ? 42
            : 0;
    }

    public static int InstanceMethods()
    {
        var marker = new GenericReceiver { Value = 17 };
        var value = new GenericValueReceiver { Value = 3 };
        long wide = marker.Forward<long>(-5000000001);
        ushort narrow = marker.Forward<ushort>(1000);
        Payload payload = value.Forward(new Payload { Tag = 7, Value = 42 });
        return
            wide == -5000000001
            && narrow == 1000
            && marker.Value == 19
            && payload.Tag == 7
            && payload.Value == 42
            && value.Value == 4
            ? 42
            : 0;
    }

    public static int Constraint() => Constrained<int>(42);

    public static int ConstraintShapes()
    {
        Marker marker = ReferenceOnly(new Marker { Value = 42 });
        Payload value = Constrained(new Payload { Tag = 17, Value = 1000 });
        return
            Defaultable(marker).Value == 42
            && BaseOnly(marker).Value == 42
            && InterfaceOnly(marker).Value == 42
            && InterfaceOnly(value).Value == 1000
            && Defaultable(value).Tag == 17
            && ReferenceOnly<string>(null) == null
            && Related<Marker, ConstraintBase>(marker).Value == 42
            && Related<LocalCode, object>(LocalCode.Value) == LocalCode.Value
            && Combined(marker).Value == 42
            && EnumOnly(LocalCode.Value) == LocalCode.Value
            ? 42
            : 0;
    }

    public static int Referenced() =>
        Library.Forward<byte>(5) + (Library.Forward<ushort>(1000) >> 3);

    static int Twice(int value) => value * 2;

    static ushort Next(ushort value) => (ushort)(value + 17);

    public static int NamedTypes()
    {
        Payload local = new Payload { Tag = 17, Value = 1000 };
        Payload returned = Library.Forward(local);
        returned = Make(returned);
        RemotePayload remote = new RemotePayload { Count = -70000, Wide = 5000000001 };
        RemotePayload remoteReturned = Wrap(Library.Forward(remote));
        Marker marker = new Marker { Value = 7 };
        Marker alias = Library.Forward(marker);
        alias.Value = 9000;
        RemoteMarker remoteMarker = Library.Forward(new RemoteMarker { Value = 1000 });
        RemoteMarker other = new RemoteMarker { Value = 42 };
        Swap(ref remoteMarker, ref other);
        IMarker view = Library.Forward<IMarker>(marker);
        IRemoteMarker remoteView = Library.Forward<IRemoteMarker>(other);
        Reader read = Library.Forward<Reader>(Twice);
        RemoteReader remoteRead = Library.Forward<RemoteReader>(Next);
        return
            returned.Tag == 17
            && returned.Value == 1000
            && local.Value == 1000
            && remoteReturned.Count == -70000
            && remoteReturned.Wide == 5000000001
            && marker.Value == 9000
            && alias == marker
            && remoteMarker.Value == 42
            && other.Value == 1000
            && Library.Forward(LocalCode.Value) == LocalCode.Value
            && Library.Forward(RemoteCode.Value) == RemoteCode.Value
            && view.Read() == 9000
            && remoteView.Read() == 1000
            && read(21) == 42
            && remoteRead(1000) == 1017
            ? 42
            : 0;
    }

    public static int PrimitiveWidths()
    {
        object marker = Wrap<object>(new Marker());
        return
            Wrap<sbyte>(-128) == -128
            && Wrap<short>(-32768) == -32768
            && Wrap<int>(int.MinValue) == int.MinValue
            && Wrap<uint>(0xfedcba98) == 0xfedcba98
            && Wrap<char>('\ud800') == '\ud800'
            && Make<bool>(true)
            && marker != null
            && Wrap<nint>((nint)(-123)) == -123
            && Wrap<nuint>((nuint)65535) == 65535
            ? 42
            : 0;
    }

    public static int Storage()
    {
        ushort first = 1000;
        ushort second = 42;
        Swap(ref first, ref second);
        string left = "Koh";
        string right = "Rust";
        Swap(ref left, ref right);
        return
            first == 42
            && second == 1000
            && left.Length == 4
            && right.Length == 3
            && Make<long>(-5000000001) == -5000000001
            && Make<byte>(231) == 231
            && Zero<ulong>() == 0
            ? 42
            : 0;
    }

    public static int Main()
    {
        byte small = Wrap<byte>(5);
        ushort wide = Wrap<ushort>(1000);
        Koh.GameBoy.Hardware.BGP = (byte)(small + (wide >> 3));
        return Koh.GameBoy.Hardware.BGP;
    }

    public static int Wide()
    {
        long signed = Recursive<long>(-5000000001, 3);
        ulong unsigned = Local<ulong>(0xfedcba9876543210);
        return signed == -5000000001 && unsigned == 0xfedcba9876543210 ? 42 : 0;
    }

    public static int References()
    {
        string text = Local<string>("Koh");
        int[] numbers = Wrap<int[]>(new int[] { 42 });
        return text.Length == 3 ? numbers[0] : 0;
    }
}

public sealed class Marker : ConstraintBase, IMarker
{
    public int Value;

    public int Read() => Value;
}

[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential,
    Pack = 1
)]
public struct Payload : IMarker
{
    public byte Tag;
    public ushort Value;

    public int Read() => Value;
}

public enum LocalCode : ushort
{
    Value = 60000,
}

public interface IMarker
{
    int Read();
}

public delegate int Reader(int value);

public class ConstraintBase { }

public class ConstraintOther { }

public struct ConstructedValue
{
    public static int Calls;
    public int Value;

    public ConstructedValue()
    {
        Calls++;
        Value = 42;
    }
}

public class GenericReceiver
{
    public int Value;

    public T Forward<T>(T value)
    {
        Value++;
        return value;
    }
}

public struct GenericValueReceiver
{
    public int Value;

    public T Forward<T>(T value)
    {
        Value++;
        return value;
    }
}

public delegate Payload PayloadReader(Payload value);

public class GenericBase : IGenericForward
{
    public int Value;

    public virtual T Forward<T>(T value)
    {
        Value++;
        return value;
    }
}

public class GenericDerived : GenericBase
{
    public override T Forward<T>(T value)
    {
        Value += 100;
        return value;
    }

    public T Direct<T>(T value) => base.Forward<T>(value);

    public Reader BaseGroup() => base.Forward<int>;
}

public interface IGenericForward
{
    T Forward<T>(T value);
}

public class GenericExplicit : IGenericForward
{
    public int Calls;

    T IGenericForward.Forward<T>(T value)
    {
        Calls++;
        return value;
    }
}

public interface IGenericDefault
{
    T Forward<T>(T value) => value;
}

public class GenericDefault : IGenericDefault { }

public class GenericForeignDerived : RemoteGenericBase
{
    public override T Forward<T>(T value)
    {
        Calls += 10;
        return value;
    }
}

public interface IMutableCounter
{
    int Next(int delta);
}

public struct MutableValue : IMutableCounter
{
    public int Value;

    public int Next(int delta) => Value += delta;
}

public struct ExplicitMutableValue : IMutableCounter
{
    public int Value;

    int IMutableCounter.Next(int delta) => Value += delta;
}

public class MutableClass : IMutableCounter
{
    public int Value;

    public int Next(int delta) => Value += delta;
}

public struct GenericForwardValue : IGenericForward
{
    public int Calls;

    public T Forward<T>(T value)
    {
        Calls++;
        return value;
    }
}

public struct ExplicitGenericForwardValue : IGenericForward
{
    public int Calls;

    T IGenericForward.Forward<T>(T value)
    {
        Calls++;
        return value;
    }
}
