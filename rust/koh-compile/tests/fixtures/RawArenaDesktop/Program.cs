using Koh.GameBoy;

public static unsafe class Program
{
    private static int Rejection(int size)
    {
        try
        {
            Mem.Alloc(size);
            return 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            return 1;
        }
        catch (OutOfMemoryException)
        {
            return 2;
        }
    }

    public static void Main()
    {
        Mem.Reset();
        byte* first = Mem.Alloc(4);
        byte* second = Mem.Alloc(4);
        first[0] = 11;
        second[0] = 22;
        long offset = first - Gb.Wram;
        int sum = first[0] + second[0];
        Mem.Reset();
        bool reused = Mem.Alloc(4) == first;
        int negative = Rejection(-1);
        int large = Rejection(4097);
        int truncated = Rejection(65536);
        Mem.Reset();
        byte* full = Mem.Alloc(4096);
        full[0] = 42;
        full[4095] = 99;
        int exhausted = Rejection(1);
        Console.WriteLine(
            $"{offset} {sum} {reused} {negative}:{large}:{truncated}:{exhausted} {Mem.Alloc(0) == full} {full[0]}:{full[4095]}"
        );
    }
}
