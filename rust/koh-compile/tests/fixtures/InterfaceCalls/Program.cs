interface IUnit
{
    int Value(int value);
}

interface IOther
{
    int Value(int value);
}

interface IState
{
    const int Sentinel = 7;
}

interface IWideContract
{
    long Wide(long value);
}

interface ILegacy
{
    byte Value();
}

sealed class LegacyUnit : ILegacy
{
    public byte Value() => 7;
}

sealed class Holder
{
    public IUnit Value;
}

delegate int ValueCall(int value);

sealed class MultiExplicit : IUnit, IOther, IState, IWideContract
{
    int IUnit.Value(int value) => value + 7;

    int IOther.Value(int value) => value + 9;

    public long Wide(long value) => value;
}

sealed class Unit : IUnit
{
    public int Value(int value) => value + 7;
}

class Parent : IUnit
{
    public virtual int Value(int value) => value + 1;
}

class Override : Parent
{
    public override int Value(int value) => value + 2;
}

class Hidden : Parent
{
    public new int Value(int value) => value + 3;
}

class Rebound : Parent, IUnit
{
    public new int Value(int value) => value + 4;
}

class Explicit : IUnit
{
    int IUnit.Value(int value) => value + 7;

    public virtual int Value(int value) => value + 2;
}

class ReboundExplicit : Explicit, IUnit { }

class OnlyExplicit : IUnit
{
    int IUnit.Value(int value) => value + 7;
}

class ReboundOnlyExplicit : OnlyExplicit, IUnit { }

interface IChild : IUnit
{
    int Extra();
}

sealed class Child : IChild
{
    public int Value(int value) => value + 3;

    public int Extra() => 4;
}

class PlainParent
{
    public int Value(int value) => value + 5;
}

sealed class Inherited : PlainParent, IUnit { }

sealed class RemoteOverride : InterfaceLibrary.RemoteBase
{
    public override int Value(int value) => value + 7;
}

sealed class RemoteLocalExplicit : InterfaceLibrary.RemoteBase, InterfaceLibrary.IRemote
{
    int InterfaceLibrary.IRemote.Value(int value) => value + 7;
}

public struct LargePacket
{
    public long P00,
        P01,
        P02,
        P03,
        P04,
        P05,
        P06,
        P07,
        P08,
        P09,
        P10,
        P11,
        P12,
        P13,
        P14,
        P15,
        P16,
        P17,
        P18,
        P19,
        P20,
        P21,
        P22,
        P23,
        P24;
    public int First,
        Last;
}

interface ILarge
{
    int Sum(LargePacket value);
    long Wide(long value);
    LargePacket Echo(LargePacket value);
}

sealed class LargeReceiver : ILarge
{
    public int Sum(LargePacket value) => value.First + value.Last;

    public long Wide(long value) => value + 7;

    public LargePacket Echo(LargePacket value)
    {
        value.First += 2;
        return value;
    }
}

interface IDefault
{
    int Value() => 7;
}

sealed class DefaultReceiver : IDefault { }

sealed class DefaultOverride : IDefault
{
    public int Value() => 42;
}

interface IGeneric<T>
{
    int Value(T value);
}

sealed class GenericReceiver : IGeneric<int>
{
    public int Value(int value) => value;
}

struct BoxedReceiver : IUnit
{
    public int Value(int value) => value;
}

public static class Program
{
    private static IUnit Current;
    private static ILegacy Legacy;
    private static IUnit[] Units;

    public static int Main()
    {
        Current = new Unit();
        return Current.Value(35);
    }

    public static int VirtualOverride()
    {
        Current = new Override();
        return Current.Value(40);
    }

    public static int HiddenMethod()
    {
        Current = new Hidden();
        return Current.Value(41);
    }

    public static int Reimplementation()
    {
        Current = new Rebound();
        return Current.Value(38);
    }

    public static int ExplicitMapping()
    {
        Current = new Explicit();
        return Current.Value(35);
    }

    public static int ReimplementedExplicit()
    {
        Current = new ReboundExplicit();
        return Current.Value(35);
    }

    public static int ReimplementedOnlyExplicit()
    {
        Current = new ReboundOnlyExplicit();
        return Current.Value(35);
    }

    public static int InheritedPublic()
    {
        Current = new Inherited();
        return Current.Value(37);
    }

    public static int InheritedInterface()
    {
        IChild value = new Child();
        Current = value;
        return Current.Value(35) + value.Extra();
    }

    public static int RemoteImplicit()
    {
        InterfaceLibrary.IRemote value = new RemoteOverride();
        return value.Value(35);
    }

    public static int RemoteExplicit()
    {
        InterfaceLibrary.IRemote value = new InterfaceLibrary.RemoteExplicit();
        return value.Value(35);
    }

    public static int RemoteMemberRef()
    {
        InterfaceLibrary.IRemote value = new RemoteLocalExplicit();
        return value.Value(35);
    }

    public static int LargeDispatch()
    {
        ILarge value = new LargeReceiver();
        LargePacket packet = default;
        packet.First = 20;
        packet.Last = 22;
        return value.Sum(packet);
    }

    public static int WideResult()
    {
        ILarge value = new LargeReceiver();
        return value.Wide(0x100000023L) == 0x10000002AL ? 42 : 1;
    }

    public static int AggregateResult()
    {
        ILarge value = new LargeReceiver();
        LargePacket packet = default;
        packet.First = 35;
        packet.Last = 5;
        LargePacket result = value.Echo(packet);
        return result.First + result.Last;
    }

    public static int Casts()
    {
        object value = new Child();
        IUnit unit = value as IUnit;
        IChild child = unit as IChild;
        return unit.Value(35) + child.Extra();
    }

    public static int NonmatchingCast()
    {
        object value = new PlainParent();
        return value is IUnit ? 1 : 42;
    }

    public static int ClassIsInstance()
    {
        object value = new Override();
        Parent parent = value as Parent;
        return parent.Value(40);
    }

    public static int NullCast()
    {
        object value = null;
        return value is IUnit || (value as IChild) != null ? 1 : 42;
    }

    public static int InvalidCast()
    {
        object value = new PlainParent();
        return ((IUnit)value).Value(35);
    }

    public static int NullReceiver()
    {
        Current = null;
        return Current.Value(35);
    }

    private static IUnit Choose(int value) => value == 0 ? new Unit() : new Override();

    public static int ConditionalFirst() => Choose(0).Value(35);

    public static int ConditionalSecond() => Choose(1).Value(40);

    public static int MultipleSlots()
    {
        MultiExplicit value = new MultiExplicit();
        Current = value;
        IOther other = value;
        return Current.Value(10) + other.Value(16);
    }

    public static int BoundInterfaceDelegate()
    {
        IUnit value = new Unit();
        ValueCall call = value.Value;
        return call(35);
    }

    public static int ReferenceStorage()
    {
        Units = new IUnit[] { new Unit(), new Override() };
        Holder holder = new Holder { Value = Units[0] };
        return holder.Value.Value(10) + Units[1].Value(23);
    }

    public static int LegacyStorage()
    {
        Legacy = new LegacyUnit();
        return Legacy.Value() + 35;
    }

    public static int LegacyHardware()
    {
        Legacy = new LegacyUnit();
        Koh.GameBoy.Hardware.SCY = Legacy.Value();
        Koh.GameBoy.Hardware.SCX = 0xEE;
        return Koh.GameBoy.Hardware.SCY == 7 && Koh.GameBoy.Hardware.SCX == 0xEE ? 42 : 1;
    }

    public static int MetadataBody()
    {
        InterfaceLibrary.IRemote value = new RemoteLocalExplicit();
        return value.Value(37);
    }

    public static int ClassMetadataBody()
    {
        InterfaceLibrary.RemoteBase value = new RemoteLocalExplicit();
        return value.Value(35);
    }

    public static int BaseCall() => new InterfaceLibrary.RemoteBase().Value(37);

    public static int DefaultInterface()
    {
        IDefault value = new DefaultReceiver();
        return value.Value() + 35;
    }

    public static int DefaultOverridden()
    {
        IDefault value = new DefaultOverride();
        return value.Value();
    }

    public static int GenericInterface()
    {
        IGeneric<int> value = new GenericReceiver();
        return value.Value(42);
    }

    public static int BoxedInterface()
    {
        IUnit value = new BoxedReceiver();
        return value.Value(42);
    }

    public static unsafe int LargeFrame()
    {
        byte* bytes = stackalloc byte[200];
        for (int i = 0; i < 200; i++)
            bytes[i] = (byte)(i + 1);
        object value = new Unit();
        Current = (IUnit)value;
        int result = Current.Value(35);
        return result + bytes[199] - 200;
    }

    public static unsafe int LargeClassFrame()
    {
        byte* bytes = stackalloc byte[200];
        for (int i = 0; i < 200; i++)
            bytes[i] = (byte)(i + 1);
        object value = new Override();
        Parent parent = value as Parent;
        int result = parent.Value(40);
        return result + bytes[199] - 200;
    }
}
