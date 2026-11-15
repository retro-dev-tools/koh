public static class Program
{
    static int Failure;

    static void Increment(ref int value) => value++;

    static void SetSigned(ref sbyte value, sbyte replacement) => value = replacement;

    static void SetShort(ref short value, short replacement) => value = replacement;

    static void AddWide(ref long value) => value += 5;

    static ref int Forward(ref int value) => ref value;

    static int ChangeCopy(int value)
    {
        Increment(ref value);
        return value;
    }

    static int SignedArgument(sbyte value)
    {
        SetSigned(ref value, 2);
        return value;
    }

    static void Descend(int depth, ref int ancestor)
    {
        int local = 100 + depth;
        int guard = 0x3D000000 | depth;
        if (depth == 0)
            Increment(ref ancestor);
        else
        {
            Descend(depth - 1, ref ancestor);
            Increment(ref local);
        }
        if (local != 100 + depth + (depth == 0 ? 0 : 1) || guard != (0x3D000000 | depth))
            Failure = 1;
    }

    public static int Main()
    {
        int input = 41;
        if (ChangeCopy(input) != 42 || input != 41)
            return 1;
        if (SignedArgument(-1) != 2)
            return 2;
        sbyte small = -1;
        SetSigned(ref small, 2);
        if (small != 2)
            return 3;
        small = 1;
        SetSigned(ref small, -2);
        if (small != -2)
            return 4;
        short word = -1;
        SetShort(ref word, 2);
        if (word != 2)
            return 5;
        word = 1;
        SetShort(ref word, -1234);
        if (word != -1234)
            return 6;
        long wide = 0x1122334455667788L;
        AddWide(ref wide);
        if (wide != 0x112233445566778DL)
            return 7;
        int root = 40;
        ref int alias = ref Forward(ref root);
        Increment(ref alias);
        Descend(8, ref root);
        if (Failure != 0 || root != 42)
            return 8;
        return root;
    }
}
