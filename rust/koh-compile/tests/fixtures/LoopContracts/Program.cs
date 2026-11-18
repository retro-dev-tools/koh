public static unsafe class Program
{
    public static long Invariant(int n, int seed, int flag)
    {
        int sum = 0,
            i = 0;
        if (flag != 0)
            for (; i < n + n; i++)
                sum = unchecked(sum + seed + seed);
        return ((long)(uint)i << 32) | (uint)sum;
    }

    public static long Variant(int n, int seed, int flag)
    {
        int sum = 0,
            i = 0;
        if (flag != 0)
            for (; i < n; i++)
                sum = unchecked(sum + i + seed);
        return ((long)(uint)i << 32) | (uint)sum;
    }

    public static long Loaded(int n, int seed, int flag)
    {
        int sum = 0,
            i = 0,
            cell = seed;
        int* pointer = &cell;
        if (flag != 0)
            for (; i < n; i++)
            {
                sum = unchecked(sum + *pointer);
                *pointer = unchecked(*pointer + 3);
            }
        sum = unchecked(sum + *pointer);
        return ((long)(uint)i << 32) | (uint)sum;
    }

    public static long Nested(int n, int seed, int flag)
    {
        int sum = 0,
            oi = 0;
        for (; oi < n; oi++)
        {
            int v = unchecked(seed + oi);
            if (flag != 0)
                for (int ii = 0; ii < 3; ii++)
                    sum = unchecked(sum + v);
            sum = unchecked(sum + v + seed);
        }
        return ((long)(uint)oi << 32) | (uint)sum;
    }

    public static long Division(int n, int seed, int flag)
    {
        int sum = 0,
            i = 0;
        if (flag != 0)
            for (; i < n; i++)
                sum = unchecked(sum + 100 / seed);
        return ((long)(uint)i << 32) | (uint)sum;
    }
}
