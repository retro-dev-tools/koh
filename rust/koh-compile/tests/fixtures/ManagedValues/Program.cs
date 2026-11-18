using Koh.GameBoy;
using Koh.GameBoy.Framework;

public sealed class Node
{
    public int Value;
}

public struct Bundle
{
    public byte[] Data;
    public string Text;
    public Node Node;
    public int Own;
}

public struct Outer
{
    public Bundle Value;
    public byte Tail;
}

public struct Branch
{
    public Branch[]? Children;
}

public sealed class Holder
{
    public Bundle Value;
}

public static unsafe class Program
{
    public static int[]? Observations;
    static Bundle saved;

    static Bundle Echo(Bundle value) => value;

    static Bundle Make() =>
        new Bundle
        {
            Data = new byte[2],
            Text = "OK",
            Node = new Node(),
            Own = 13,
        };

    public static int Main()
    {
        if (Cgb.IsColor())
            *(byte*)0xFF70 = 2;
        int[] output = new int[12];
        Observations = output;
        Bundle zero = default;
        if (zero.Data != null || zero.Text != null || zero.Node != null)
            return 99;
        Bundle first = Make();
        Bundle second = Echo(first);
        second.Own = 99;
        second.Data[1] = 7;
        second.Node.Value = 8;
        saved = second;
        Bundle[] array = new Bundle[2];
        array[0] = first;
        array[1] = saved;
        array[0].Own = 17;
        Holder holder = new Holder { Value = first };
        holder.Value.Own = 27;
        Outer nested = new Outer { Value = first, Tail = 5 };
        output[0] = first.Own;
        output[1] = second.Own;
        output[2] = first.Data[1];
        output[3] = first.Node.Value;
        output[4] = first.Text.Length;
        output[5] = first.Text[0];
        output[6] = array[0].Own;
        output[7] = array[1].Own;
        output[8] = holder.Value.Own;
        output[9] = nested.Value.Own;
        holder.Value.Data[1] = 11;
        output[10] = array[1].Data[1];
        output[11] = saved.Node.Value;
        if (Cgb.IsColor() && (*(byte*)0xFF70 & 7) != 2)
            return 98;
        return 42;
    }

    public static int AssetCount() => TileAsset.Define(new byte[32]).TileCount + 40;

    public static int RecursiveArray()
    {
        Branch root = default;
        root.Children = new Branch[1];
        root.Children[0] = root;
        return root.Children[0].Children == root.Children ? 42 : 99;
    }

    static ref byte[] DataAlias(ref Bundle value) => ref value.Data;

    static ref string TextAlias(ref Bundle value) => ref value.Text;

    static ref Node NodeAlias(ref Bundle value) => ref value.Node;

    static void Replace(ref byte[] value) => value = new byte[] { 40, 2 };

    public static int ReferenceAliases()
    {
        if (Cgb.IsColor())
            *(byte*)0xFF70 = 2;
        Bundle value = Make();
        ref byte[] data = ref DataAlias(ref value);
        data = new byte[] { 40, 2 };
        ref string text = ref TextAlias(ref value);
        text = "NEW";
        ref Node node = ref NodeAlias(ref value);
        node = new Node { Value = 7 };
        byte[] local = value.Data;
        Replace(ref local);
        if (
            value.Data[0] + value.Data[1] != 42
            || value.Text.Length != 3
            || value.Node.Value != 7
            || local[0] + local[1] != 42
        )
            return 99;
        if (Cgb.IsColor() && (*(byte*)0xFF70 & 7) != 2)
            return 98;
        return 42;
    }
}
