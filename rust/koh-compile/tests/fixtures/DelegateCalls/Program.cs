using System;

public sealed class GenericCallbackBox<T>
{
    public Func<T, T> Callback;

    public GenericCallbackBox(Func<T, T> callback) => Callback = callback;

    public T Read(T value) => Callback(value);

    public U Select<U>(T value, Func<T, U> selector) => selector(value);

    public Func<T, T> Capture(T value) => ignored => value;

    public T Echo(T value) => value;
}

public static unsafe class GenericCallbackRules
{
    public static int Int() => new GenericCallbackBox<int>(x => x + 5).Read(37);

    public static int Byte() => new GenericCallbackBox<byte>(x => (byte)(x + 2)).Read(254) + 42;

    public static int Signed() => new GenericCallbackBox<short>(x => (short)(x - 5)).Read(-7) + 54;

    public static int Long()
    {
        long value = new GenericCallbackBox<long>(x => x + 5).Read(0x1234567800000025L);
        *(long*)0xDF08 = value;
        return (int)value;
    }

    public static int Mixed()
    {
        var box = new GenericCallbackBox<int>(x => x);
        long value = box.Select<long>(42, x => 0x1234567800000000L + x);
        *(long*)0xDF08 = value;
        return (int)value;
    }

    public static int Array()
    {
        int[] values = { 7, 42 };
        return new GenericCallbackBox<int[]>(x => x).Read(values)[1];
    }

    public static int Captured()
    {
        var box = new GenericCallbackBox<long>(x => x);
        Func<long, long> callback = box.Capture(0x123456780000002AL);
        long value = callback(0);
        *(long*)0xDF08 = value;
        return (int)value;
    }

    public static int OwnerGroup()
    {
        var box = new GenericCallbackBox<int>(x => x);
        Func<int, int> callback = box.Echo;
        return callback(42);
    }
}

public delegate long WideCallback(long value, ref int state);
public delegate int ByteCallback(byte value);

public enum FirstEnum
{
    Value = 1,
}

public enum SecondEnum
{
    Value = 1,
}

public class Counter
{
    public int Value;

    public virtual int Add(int value) => Value += value;

    public int PlainAdd(int value) => Value += value;

    public T Identity<T>(T value) => value;
}

public sealed class DoubleCounter : Counter
{
    public override int Add(int value) => Value += 2 * value;
}

public sealed class CallbackHolder
{
    public Action Callback;

    public CallbackHolder(Action callback) => Callback = callback;

    public void Run() => Callback();
}

public static class Program
{
    private static int State;
    private static Action Stored;
    private static Func<int, int> BoundStored;

    private static void First() => State += 7;

    private static void Second() => State += 35;

    private static void Apply(Action callback) => callback();

    private static long Wide(long value, ref int state)
    {
        state += 7;
        return value ^ 0x123456789ABCDEFL;
    }

    public static int StaticTarget()
    {
        State = 0;
        Stored = First;
        Apply(Stored);
        Stored = Second;
        Stored();
        return State;
    }

    public static int CapturedTarget()
    {
        int value = 1;
        Action callback = () => value += 20;
        CallbackHolder holder = new CallbackHolder(callback);
        holder.Run();
        callback();
        value += 1;
        return value;
    }

    public static int StaticLambdas()
    {
        State = 0;
        Stored = () => State += 1;
        Stored();
        Stored = () => State += 10;
        Stored();
        Stored();
        return State == 21 ? 42 : -1;
    }

    public static int SealedCall()
    {
        DoubleCounter counter = new DoubleCounter();
        return counter.Add(21);
    }

    public static int SealedGroup()
    {
        DoubleCounter counter = new DoubleCounter();
        Func<int, int> callback = counter.Add;
        return callback(21);
    }

    public static int GenericGroup()
    {
        var counter = new Counter();
        Func<int, int> callback = counter.Identity<int>;
        return callback(21);
    }

    public static int InstanceTarget()
    {
        Counter counter = new DoubleCounter();
        Func<int, int> callback = counter.Add;
        callback(7);
        return callback(14);
    }

    public static unsafe int WideTarget()
    {
        byte* padding = stackalloc byte[200];
        padding[0] = 7;
        padding[199] = 35;
        WideCallback callback = Wide;
        int state = 35;
        long result = callback(-0x23456789ABCDEF1L, ref state);
        return
            result == (-0x23456789ABCDEF1L ^ 0x123456789ABCDEFL) && padding[0] + padding[199] == 42
            ? state
            : -1;
    }

    private static Func<int, int> MakeAdder(int bias) => value => value + bias;

    private static int ApplyFunction(Func<int, int> callback, int value) => callback(value);

    private static int AcceptByte(byte value) => value ^ 0xD4;

    private static int AcceptInt(int value) => value ^ 0xD4;

    private static int AcceptEnum(FirstEnum value) => (int)value + 41;

    public static int FunctionTargets()
    {
        int bias = 5;
        Func<int, int> plain = value => value + 1;
        Func<int, int> captured = value => value + bias;
        bool ok =
            plain(5) == 6
            && ApplyFunction(captured, 10) == 15
            && ApplyFunction(value => value * 2, 21) == 42
            && ApplyFunction(value => value * 2, 3) == 6;
        bias = 7;
        return ok && captured(10) == 17 ? 42 : -1;
    }

    public static int ConstructorFields()
    {
        State = 0;
        byte captured = 40;
        CallbackHolder first = new CallbackHolder(() => State = captured + 2);
        CallbackHolder second = new CallbackHolder(() => State += 7);
        first.Run();
        if (State != 42)
            return -1;
        second.Run();
        if (State != 49)
            return -1;
        captured = 41;
        first.Run();
        return State == 43 ? 42 : -1;
    }

    public static int ReturnedAndArray()
    {
        Func<int, Func<int, int>> factory = MakeAdder;
        Func<int, int> first = factory(39);
        Func<int, int> second = factory(7);
        Func<int, int>[] callbacks = { first, second };
        return callbacks[0](3) == 42 && callbacks[1](35) == 42 && first(1) == 40 ? 42 : -1;
    }

    public static int ByteTarget()
    {
        ByteCallback callback = AcceptByte;
        return callback(254);
    }

    public static int EnumTarget()
    {
        Func<FirstEnum, int> callback = AcceptEnum;
        return callback(FirstEnum.Value);
    }

    public static int NullInvoke()
    {
        Action callback = null;
        callback();
        return 42;
    }

    public static int NullBinding()
    {
        Counter counter = null;
        BoundStored = counter.Add;
        return 42;
    }

    public static int NullNonVirtualBinding()
    {
        Counter counter = null;
        BoundStored = counter.PlainAdd;
        return 42;
    }

    public static int Main() =>
        StaticTarget() == 42
        && StaticLambdas() == 42
        && CapturedTarget() == 42
        && InstanceTarget() == 42
        && WideTarget() == 42
        && FunctionTargets() == 42
        && ConstructorFields() == 42
        && ReturnedAndArray() == 42
        && ByteTarget() == 42
        && EnumTarget() == 42
            ? 42
            : -1;
}

public static class NullDelegateShapes
{
    private static Action Identity(Action callback) => callback;

    private static ref Func<int, int> Cell(ref Func<int, int> callback) => ref callback;

    private static Func<byte, int> ByteFunction(Func<byte, int> callback) => callback;

    private static Action<FirstEnum> FirstFunction(Action<FirstEnum> callback) => callback;

    private static Action<SecondEnum> SecondFunction(Action<SecondEnum> callback) => callback;

    private static Action<int> IntegerFunction(Action<int> callback) => callback;

    public static int Main()
    {
        Action action = null;
        Func<int, int> function = null;
        WideCallback wide = null;
        Func<byte, int> narrow = null;
        Action<FirstEnum> first = null;
        Action<SecondEnum> second = null;
        Action<int> integer = null;
        CallbackHolder holder = new CallbackHolder(action);
        Action[] callbacks = new Action[2];
        callbacks[1] = Identity(holder.Callback);
        return
            Identity(holder.Callback) == null
            && Cell(ref function) == null
            && wide == null
            && ByteFunction(narrow) == null
            && FirstFunction(first) == null
            && SecondFunction(second) == null
            && IntegerFunction(integer) == null
            && callbacks[0] == null
            && callbacks[1] == null
            ? 42
            : -1;
    }
}
