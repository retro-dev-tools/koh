using Koh.GameBoy;

public class Parent
{
    public byte Extra;
}

public sealed class Box : Parent
{
    public int Value;
    public readonly string Text;
    public readonly byte[] Bytes;
    public Box? Next;

    public Box(int value, string text)
    {
        Value = value;
        Text = text;
        Bytes = new byte[2];
        Bytes[1] = 2;
    }

    public int Read() => Value + Text.Length + Bytes[1] + Extra;

    public int Constant() => 42;
}

public sealed class Other : Parent
{
    public Other()
    {
        Extra = 42;
    }
}

public class Banner
{
    readonly string text;
    readonly ushort score;

    public Banner(string value, ushort number)
    {
        text = value;
        score = number;
    }

    public byte Length() => (byte)text.Length;

    public byte CharAt(byte index) => (byte)text[index];

    public ushort Score() => score;
}

public class Enemy
{
    public byte Hp;
    public ushort Score;
}

public sealed class Boss : Enemy
{
    public byte Phase;
    public ushort Armor;
}

public static unsafe class Program
{
    static Box? shared;

    public static int StaticReference()
    {
        shared = Make(42);
        return shared.Value;
    }

    static Box Make(int value) => new Box(value, "OK");

    public static int Main()
    {
        if (Cgb.IsColor())
            *(byte*)0xFF70 = 2;
        Box first = Make(10);
        Box second = Make(20);
        if (first == second || first.Extra != 0 || first.Next != null)
            return 99;
        Parent parent = first;
        parent.Extra = 1;
        Box alias = first;
        alias.Value = 13;
        first.Next = second;
        if (Cgb.IsColor() && (*(byte*)0xFF70 & 7) != 2)
            return 98;
        return first.Read() + first.Next.Read();
    }

    public static int NullCall()
    {
        Box missing = null!;
        return missing.Constant();
    }

    public static int NullField()
    {
        Box missing = null!;
        return missing.Value;
    }

    public static int Exhaust()
    {
        for (int i = 0; i < 300; i++)
            _ = Make(i);
        return 99;
    }

    static Parent Choose(bool first) => first ? Make(10) : new Other();

    public static int MergeTrue() => Choose(true).Extra + 42;

    public static int MergeFalse() => Choose(false).Extra;

    static Box? Maybe(bool first) => first ? Make(10) : null;

    public static int NullMerge() => Maybe(false) == null ? 42 : 99;

    public static int ObjectMerge() => Maybe(true)!.Value + 32;

    static Box Chain(int count)
    {
        Box result = Make(count);
        if (count != 0)
            result.Next = Chain(count - 1);
        return result;
    }

    public static int Recursive()
    {
        int total = 0;
        for (Box? current = Chain(5); current != null; current = current.Next)
            total += current.Value;
        return total + 27;
    }

    static ref int Alias(Box box) => ref box.Value;

    public static int FieldReference()
    {
        Box box = Make(1);
        ref int value = ref Alias(box);
        value = 42;
        return box.Value;
    }

    public static int ArrayOfObjects()
    {
        Box[] values = new Box[2];
        values[0] = Make(10);
        values[1] = Make(20);
        if (values[0] == values[1])
            return 99;
        values[0].Value = 14;
        return values[0].Read() + values[1].Read();
    }

    public static int NullElement() => (new Box[1])[0]!.Constant();

    public static int BannerFields()
    {
        var win = new Banner("YOU WIN!", 2048);
        var lose = new Banner("GAME OVER", 512);
        byte* output = Mem.Alloc(16);
        Mem.Fill(output, 0, 16);
        output[0] = win.Length();
        output[1] = lose.Length();
        output[2] = win.CharAt(0);
        output[3] = win.CharAt(4);
        output[4] = lose.CharAt(0);
        output[5] = lose.CharAt(5);
        *(ushort*)(output + 6) = win.Score();
        *(ushort*)(output + 8) = lose.Score();
        return 42;
    }

    public static int InheritedFields()
    {
        Boss boss = new Boss();
        boss.Hp = 42;
        boss.Score = 5000;
        boss.Phase = 3;
        boss.Armor = 700;
        byte* output = Mem.Alloc(16);
        Mem.Fill(output, 0, 16);
        output[0] = boss.Hp;
        *(ushort*)(output + 1) = boss.Score;
        output[3] = boss.Phase;
        *(ushort*)(output + 4) = boss.Armor;
        Enemy parent = boss;
        output[6] = parent.Hp;
        *(ushort*)(output + 7) = parent.Score;
        parent.Hp = 77;
        output[9] = boss.Hp;
        output[10] = boss.Phase;
        return 42;
    }
}
