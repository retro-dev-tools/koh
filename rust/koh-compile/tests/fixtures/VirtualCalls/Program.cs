public abstract class Shape
{
    public abstract int Area(int scale);

    public virtual byte Kind() => 0x82;

    public virtual int Kind<T>(T value) => throw new System.InvalidOperationException();
}

public sealed class Square : Shape
{
    public int Side;

    public override int Area(int scale) => Side * Side * scale;

    public override byte Kind() => 0xE3;
}

public sealed class Rectangle : Shape
{
    public override int Area(int scale) => 6 * scale;
}

public sealed class Triangle : Shape
{
    public int Basis,
        Height;

    public override int Area(int scale) => Basis * Height * scale / 2;

    public override byte Kind() => 0xA3;
}

public sealed class Factory : Shape
{
    public override int Area(int scale)
    {
        Shape created = new Rectangle();
        return created.Area(scale);
    }
}

public sealed class Unused : Shape
{
    public override int Area(int scale) => throw new System.InvalidOperationException();
}

public abstract class WideBase
{
    public abstract long Mix(long value, byte low, ref int state);
}

public sealed class WideValue : WideBase
{
    public override long Mix(long value, byte low, ref int state)
    {
        state = 42;
        return value | low;
    }
}

public class ReturnBase
{
    public virtual Shape Value() => new Rectangle();
}

public sealed class CovariantReturn : ReturnBase
{
    public override Square Value() => new Square();
}

public class First
{
    public virtual int Value() => 10;
}

public class Second : First
{
    public override int Value() => base.Value() + 4;
}

public class Third : Second
{
    public new virtual int Value() => 20;
}

public sealed class Fourth : Third
{
    public override int Value() => 28;
}

public class Constructing
{
    public int Observed;

    public Constructing()
    {
        Observed = Value();
    }

    public virtual int Value() => -1;
}

public sealed class Constructed : Constructing
{
    public int Later;

    public Constructed()
    {
        Later = 42;
    }

    public override int Value() => Later;
}

public static unsafe class Program
{
    public static int[]? Observations;

    static void Start()
    {
        if (Koh.GameBoy.Cgb.IsColor())
            *(byte*)0xFF70 = 2;
    }

    static int Finish(int value) =>
        Koh.GameBoy.Cgb.IsColor() && (*(byte*)0xFF70 & 7) != 2 ? 99 : value;

    static Shape? current;
    static First? first;
    static Third? third;

    public static int Main()
    {
        Start();
        int[] observations = new int[8];
        Observations = observations;
        int value = 0;
        for (int index = 0; index < 3; index++)
        {
            if (index == 0)
                current = new Square { Side = 3 };
            else if (index == 1)
                current = new Rectangle();
            else
                current = new Triangle { Basis = 4, Height = 2 };
            int area = current.Area(index == 2 ? 1 : 2);
            int kind = current.Kind();
            observations[index * 2] = area;
            observations[index * 2 + 1] = kind;
            value += area + kind;
        }
        return Finish(value == 554 ? 42 : 99);
    }

    public static int Slots()
    {
        Start();
        Fourth value = new Fourth();
        first = value;
        third = value;
        return Finish(first.Value() + third.Value());
    }

    public static int Constructor()
    {
        Start();
        Constructed value = new Constructed();
        return Finish(value.Observed + value.Later);
    }

    public static int Null()
    {
        Start();
        current = null;
        return current!.Kind();
    }

    public static int Transitive()
    {
        Start();
        current = new Factory();
        return Finish(current.Area(7));
    }

    static object MakeSquare() => new Square { Side = 7 };

    static object MakeRectangle() => new Rectangle();

    public static int Cast()
    {
        Start();
        Shape value = (Shape)MakeSquare();
        return Finish(value.Area(1) - 7);
    }

    public static int InvalidCast()
    {
        Start();
        return ((Square)MakeRectangle()).Area(1);
    }

    public static int NullCast()
    {
        Start();
        object? value = null;
        return Finish((Shape?)value == null ? 42 : 99);
    }

    public static int Wide()
    {
        Start();
        WideBase value = new WideValue();
        int state = 0;
        long result = value.Mix(0x1122334455667700L, 0x88, ref state);
        return Finish(result == 0x1122334455667788L && state == 42 ? 42 : 99);
    }

    public static int Covariant() => new CovariantReturn().Value().Area(2);
}
