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

    public static int Expanding() => Expand<int>();

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
