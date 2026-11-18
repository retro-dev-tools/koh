using System.Runtime.CompilerServices;

public static class RootModule
{
    public static int Runs;

    [ModuleInitializer]
    public static void Initialize()
    {
        Runs++;
        Trace.Append(1);
    }
}

public static class Program
{
    static readonly int Value = Compute();

    static Program() => Trace.Append(4);

    static int Compute()
    {
        Trace.Append(3);
        return 17;
    }

    static int RightHandSide()
    {
        Trace.Append(8);
        return 42;
    }

    public static int Main()
    {
        if (Value != 17 || RootModule.Runs != 1 || RuntimeModule.Runs != 1)
            return 1;
        if (Trace.Log != 2134)
            return 2;
        if (Lazy.Pure() != 7 || Trace.LazyRuns != 0 || Dormant.Pure() != 7)
            return 7;
        if (Lazy.Read() != 9 || Lazy.Read() != 9 || Trace.LazyRuns != 1)
            return 8;
        if (Ordered.Read() != 17 || Ordered.Read() != 17)
            return 3;
        if (Ordered.DuringInitialization != 17 || Ordered.Runs != 1)
            return 4;
        if (CycleA.Value != 2 || CycleB.Value != 1)
            return 5;
        if (Trace.Log != 2134567 || RuntimeModule.Runs != 1)
            return 6;
        StoreTarget.Value = RightHandSide();
        if (StoreTarget.Value != 42 || Trace.Log != 213456789)
            return 9;
        return 42;
    }
}
