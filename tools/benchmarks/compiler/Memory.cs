public static class Program
{
    private struct Pair
    {
        public ushort X;
        public ushort Y;
    }

    private static void Accumulate(ref Pair pair, byte value)
    {
        pair.X = (ushort)(pair.X + value);
        pair.Y = (ushort)(pair.Y ^ (pair.X >> 1));
    }

    private static uint Run()
    {
        var values = new byte[64];
        for (int index = 0; index < values.Length; index++)
            values[index] = (byte)(index * 3 + 1);
        var pair = new Pair { X = 7, Y = 11 };
        for (int index = 0; index < values.Length; index++)
            Accumulate(ref pair, values[index]);
        return (uint)pair.X | ((uint)pair.Y << 16);
    }

    public static unsafe void Main()
    {
        *(uint*)0xC900 = Run();
        while (true) { }
    }
}
