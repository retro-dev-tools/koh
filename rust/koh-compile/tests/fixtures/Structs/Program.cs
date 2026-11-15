using System.Runtime.InteropServices;
using Koh.GameBoy;
using Koh.GameBoy.Graphics;

public static unsafe class Program
{
    [KohIntrinsic("register", 0xFF70)]
    static byte Bank
    {
        get => 0;
        set { }
    }

    public static int Banked()
    {
        Bank = 2;
        int result = Main();
        return (Bank & 7) == 2 ? result : -1;
    }

    public static int Main()
    {
        Video.Init();
        Sprites.Get(3, out Sprite sprite);
        sprite.Set(4, 5, 7);
        sprite.Move(-2, 9);
        sprite.SetAttr(ObjAttr.FlipX);
        sprite.SetTile(11);
        return sprite.Index;
    }

    static Packet Global;

    static Constructed Create(int value) => new Constructed(value);

    static int Remember(Constructed value, ref Constructed previous, int index)
    {
        if (index > 0 && previous.Value != index - 1)
            return -100;
        previous = value;
        value.Value = -5;
        return index;
    }

    static Constructed Reshape(Constructed value) => new Constructed(value, 6);

    static Constructed Recursive(int depth) =>
        depth == 0 ? Create(3) : new Constructed(Recursive(depth - 1), 2);

    static readonly Constructed Cached = new Constructed(8);

    public static int UnsupportedGeneric() => new GenericConstructed<int>(1).Value;

    public static int UnsupportedClass() => new ReferenceConstructed(1).Value;

    public static int BankedConstructors()
    {
        Bank = 2;
        int result = Constructors();
        return (Bank & 7) == 2 ? result : -99;
    }

    public static int Constructors()
    {
        Constructed empty = default;
        Constructed initialized = new Constructed();
        Constructed first = Create(37);
        Constructed second = Reshape(first);
        Constructed third = Create(38);
        Constructed recursive = Recursive(2);
        Immutable immutable = new Immutable(123);
        int counter = 9;
        Constructed borrowed = new Constructed(ref counter);
        int total = 0;
        Constructed previous = default;
        for (int i = 0; i < 3; i++)
            total += Remember(new Constructed(i), ref previous, i);
        byte* padding = (byte*)&first;
        for (int i = 0; i < sizeof(Constructed); i++)
            if ((i >= 4 && i < 8 || i >= 20) && padding[i] != 0)
                return -3;
        return
            empty.Value == 0
            && initialized.Value == 11
            && initialized.Extra == 0
            && initialized.Wide == 0
            && first.Value == 37
            && first.Wide == 0x0123456789ABCDEF
            && second.Value == 43
            && second.Extra == 6
            && second.Wide == first.Wide
            && third.Value == 38
            && recursive.Value == 7
            && recursive.Extra == 2
            && immutable.Value == 123
            && immutable.Data.Body.Score == 123
            && borrowed.Value == 9
            && counter == 10
            && total == 3
            && previous.Value == 2
            && Cached.Value == 8
            ? 42
            : -1;
    }

    static Packet Make()
    {
        Packet value = default;
        value.Tag = 0xA5;
        value.Delta = -123;
        value.Score = 100;
        value.Wide = 0x0123456789ABCDEF;
        return value;
    }

    static Packet Change(Packet value)
    {
        value.Tag = 0x5A;
        value.Delta += 5;
        value.Score += 7;
        value.Wide ^= 0xFF;
        return value;
    }

    static Nested Echo(Nested value) => value;

    static Overlay EchoOverlay(Overlay value) => value;

    public static int BankedValues()
    {
        Bank = 2;
        int result = Values();
        return (Bank & 7) == 2 ? result : -99;
    }

    public static int Values()
    {
        Packet first = Make();
        Packet second = Change(first);
        Global = second;
        ref int score = ref Global.Score;
        score++;
        Nested nested = default;
        nested.Body = Global;
        nested.Tail = 0xBEEF;
        Nested copy = Echo(nested);
        nested.Body.Score = 0;
        Overlay overlay = default;
        overlay.Bits = 0x89ABCDEF;
        overlay.Tail = 0x7E;
        Overlay other = EchoOverlay(overlay);
        overlay.Bits = 0;
        byte* padding = (byte*)&first;
        for (int i = 16; i < sizeof(Packet); i++)
            if (padding[i] != 0)
                return -3;
        return
            first.Tag == 0xA5
            && first.Delta == -123
            && first.Score == 100
            && first.Wide == 0x0123456789ABCDEF
            && second.Tag == 0x5A
            && second.Delta == -118
            && second.Score == 107
            && second.Wide == 0x0123456789ABCD10
            && Global.Score == 108
            && copy.Body.Score == 108
            && copy.Tail == 0xBEEF
            && other.Bits == 0x89ABCDEF
            && other.Signed == unchecked((short)0xCDEF)
            && other.Tail == 0x7E
            && sizeof(Packet) == 64
            && sizeof(Overlay) == 5
            ? 42
            : -1;
    }

    public static int NativeFields()
    {
        Bank = 2;
        Packet* value = (Packet*)0xD000;
        value->Tag = 7;
        value->Delta = -2;
        value->Score = 0x12345678;
        value->Wide = 0x0123456789ABCDEF;
        return
            value->Tag == 7
            && value->Delta == -2
            && value->Score == 0x12345678
            && value->Wide == 0x0123456789ABCDEF
            && (!Cgb.IsColor() || (Bank & 7) == 2)
            ? 42
            : -1;
    }

    static void Copy(ref Packet destination, ref Packet source) => destination = source;

    public static int BankedScalarsAndOverlap()
    {
        Bank = 2;
        int result = ScalarsAndOverlap();
        return (Bank & 7) == 2 ? result : -99;
    }

    public static int ScalarsAndOverlap()
    {
        ScalarFields scalar = default;
        scalar.Flag = true;
        scalar.Signed = -7;
        scalar.Glyph = '\u1234';
        scalar.Address = (nuint)0xBEEF;
        scalar.Wide = 0xFEDCBA9876543210;
        scalar.Set(6);
        Overlap overlap = default;
        overlap.First = Make();
        Copy(ref overlap.Second, ref overlap.First);
        return
            scalar.Flag
            && scalar.Signed == -7
            && scalar.Glyph == '\u1234'
            && scalar.Address == (nuint)0xBEEF
            && scalar.Wide == 0xFEDCBA9876543210
            && scalar.Tag == 7
            && sizeof(ScalarFields) == 16
            && overlap.Second.Tag == 0xA5
            && overlap.Second.Delta == -123
            && overlap.Second.Score == 100
            && overlap.Second.Wide == 0x0123456789ABCDEF
            ? 42
            : -1;
    }
}

[StructLayout(LayoutKind.Sequential, Size = 64)]
public struct Packet
{
    public byte Tag;
    public short Delta;
    public int Score;
    public long Wide;
}

public struct Nested
{
    public Packet Body;
    public ushort Tail;
}

[StructLayout(LayoutKind.Explicit, Size = 5)]
public struct Overlay
{
    [FieldOffset(0)]
    public uint Bits;

    [FieldOffset(0)]
    public short Signed;

    [FieldOffset(4)]
    public byte Tail;
}

public struct ScalarFields
{
    public bool Flag;
    public sbyte Signed;
    public char Glyph;
    public nuint Address;
    public byte Tag;
    public ulong Wide;

    static void Bump(ref byte value) => value++;

    public void Set(byte value)
    {
        Bump(ref value);
        Tag = value;
    }
}

[StructLayout(LayoutKind.Explicit, Size = 72)]
public struct Overlap
{
    [FieldOffset(0)]
    public Packet First;

    [FieldOffset(8)]
    public Packet Second;
}

[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct Constructed
{
    public int Value;
    public long Wide;
    public int Extra;

    public Constructed()
    {
        Value = 11;
    }

    public Constructed(int value)
    {
        Value = value;
        Wide = 0x0123456789ABCDEF;
    }

    public Constructed(Constructed value, int delta)
        : this(value.Value + delta)
    {
        Wide = value.Wide;
        Extra = delta;
    }

    public Constructed(ref int counter)
        : this(counter)
    {
        counter++;
    }
}

public readonly struct Immutable
{
    public readonly int Value;
    public readonly Nested Data;

    public Immutable(int value)
    {
        Value = value;
        Packet packet = default;
        packet.Score = value;
        Nested data = default;
        data.Body = packet;
        Data = data;
    }
}

public struct GenericConstructed<T>
{
    public T Value;

    public GenericConstructed(T value)
    {
        Value = value;
    }
}

public sealed class ReferenceConstructed
{
    public int Value;

    public ReferenceConstructed(int value)
    {
        Value = value;
    }
}
