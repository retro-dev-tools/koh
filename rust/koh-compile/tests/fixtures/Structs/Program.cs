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
