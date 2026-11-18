interface IBase
{
    int Value(int value) => value + 1;
}

interface ILeft : IBase
{
    int IBase.Value(int value) => value + 3;
    int Invoke(int value) => ((IBase)this).Value(value);
    ValueCall Bind() => ((IBase)this).Value;
}

interface IRight : IBase
{
    int IBase.Value(int value) => value + 5;
}

interface IDiamond : ILeft, IRight
{
    int IBase.Value(int value) => value + 7;
}

interface IReverse : IRight, ILeft
{
    int IBase.Value(int value) => value + 9;
}

interface ILink : IBase { }

interface IShared : ILeft, ILink { }

interface IReabstract : IBase
{
    abstract int IBase.Value(int value);
}

class Basic : IBase { }

sealed class Left : ILeft { }

sealed class Right : IRight { }

sealed class Diamond : IDiamond { }

sealed class Reverse : IReverse { }

sealed class Shared : IShared { }

sealed class Inherited : Basic { }

sealed class Hidden : Basic
{
    public int Value(int value) => value + 9;
}

sealed class Rebound : Basic, IBase
{
    public int Value(int value) => value + 7;
}

sealed class ClassWins : ILeft, IRight
{
    public int Value(int value) => value + 7;
}

sealed class Reabstract : IReabstract
{
    public int Value(int value) => value + 7;
}

class ConcreteBase : IBase
{
    public virtual int Value(int value) => value + 1;
}

sealed class ConcreteDerived : ConcreteBase, ILeft { }

interface ICalculator
{
    int Core(int value);
    int Value(int value) => Core(value) + 2;
}

sealed class Calculator : ICalculator
{
    public int Core(int value) => value + 5;
}

interface IHelpers
{
    private int Bias() => 7;
    int Value(int value) => value + Bias();
    int Count => 42;
}

sealed class Helpers : IHelpers { }

static class Trace
{
    public static int Value;
}

interface IInitialized
{
    static IInitialized()
    {
        Trace.Value = 35;
    }

    int Value() => Trace.Value + 7;
}

sealed class Initialized : IInitialized { }

interface IDeadDefault
{
    int Good() => 42;
    float Unused() => 1.5f;
}

sealed class DeadDefault : IDeadDefault { }

public struct Packet
{
    public long A00,
        A01,
        A02,
        A03,
        A04,
        A05,
        A06,
        A07,
        A08,
        A09;
    public long A10,
        A11,
        A12,
        A13,
        A14,
        A15,
        A16,
        A17,
        A18,
        A19;
    public long A20,
        A21,
        A22,
        A23,
        A24;
    public int First,
        Last;
}

interface IWidths
{
    long Wide(long value) => value + 7;
    int Change(ref int value)
    {
        value += 7;
        return value;
    }
    Packet Echo(Packet value)
    {
        value.Last += 2;
        return value;
    }
}

sealed class Widths : IWidths { }

delegate int ValueCall(int value);

public static class Program
{
    static IBase Current;

    public static int Main()
    {
        Current = new Basic();
        return Current.Value(41);
    }

    public static int Left()
    {
        Current = new Left();
        return Current.Value(39);
    }

    public static int Right()
    {
        Current = new Right();
        return Current.Value(37);
    }

    public static int Diamond()
    {
        Current = new Diamond();
        return Current.Value(35);
    }

    public static int Reverse()
    {
        Current = new Reverse();
        return Current.Value(33);
    }

    public static int Shared()
    {
        Current = new Shared();
        return Current.Value(39);
    }

    public static int Inherited()
    {
        Current = new Inherited();
        return Current.Value(41);
    }

    public static int Hidden()
    {
        Current = new Hidden();
        return Current.Value(41);
    }

    public static int Rebound()
    {
        Current = new Rebound();
        return Current.Value(35);
    }

    public static int ClassWins()
    {
        Current = new ClassWins();
        return Current.Value(35);
    }

    public static int Reabstract()
    {
        Current = new Reabstract();
        return Current.Value(35);
    }

    public static int InheritedClassWins()
    {
        Current = new ConcreteDerived();
        return Current.Value(41);
    }

    public static int NestedCall()
    {
        ICalculator value = new Calculator();
        return value.Value(35);
    }

    public static int PrivateHelper()
    {
        IHelpers value = new Helpers();
        return value.Value(35);
    }

    public static int Property()
    {
        IHelpers value = new Helpers();
        return value.Count;
    }

    public static int Initializer()
    {
        IInitialized value = new Initialized();
        int first = value.Value();
        Trace.Value = 36;
        int second = value.Value();
        return first == 42 && second == 43 && Trace.Value == 36 ? 42 : 1;
    }

    public static int DeadDefault()
    {
        IDeadDefault value = new DeadDefault();
        return value.Good();
    }

    public static int Wide()
    {
        IWidths value = new Widths();
        return value.Wide(0x100000023L) == 0x10000002AL ? 42 : 1;
    }

    public static int ByReference()
    {
        IWidths value = new Widths();
        int number = 35;
        int result = value.Change(ref number);
        return number == 42 ? result : 1;
    }

    public static int Aggregate()
    {
        IWidths value = new Widths();
        Packet packet = default;
        packet.First = 35;
        packet.Last = 5;
        Packet result = value.Echo(packet);
        return packet.First == 35 && packet.Last == 5 && result.First == 35 && result.Last == 7
            ? 42
            : 1;
    }

    public static int BoundDelegate()
    {
        Current = new Left();
        ValueCall value = Current.Value;
        Current = new Right();
        return value(39);
    }

    public static int Referenced()
    {
        InterfaceLibrary.IRemoteRoot value = new InterfaceLibrary.RemoteDefault();
        return value.Value(35);
    }

    public static int NullReceiver()
    {
        Current = null;
        return Current.Value(41);
    }

    public static int FinalBodyCall()
    {
        ILeft value = new Diamond();
        return value.Invoke(39);
    }

    public static int FinalBodyDelegate()
    {
        ILeft value = new Diamond();
        return value.Bind()(39);
    }
}
