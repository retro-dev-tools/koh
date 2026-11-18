using System.Runtime.CompilerServices;

public static class Trace
{
    public static int Log;
    public static int LazyRuns;

    public static void Append(int digit) => Log = Log * 10 + digit;
}

public static class Lazy
{
    public static int Value = Initialize();

    static int Initialize()
    {
        Trace.LazyRuns++;
        return 9;
    }

    public static int Pure() => 7;

    public static int Read() => Value;
}

public static class Dormant
{
    public static object Unused = new object();

    public static int Pure() => 7;
}

public static class RuntimeModule
{
    public static int Runs;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2255",
        Justification = "Compiler fixture verifies module initialization in a referenced library."
    )]
    [ModuleInitializer]
    public static void Initialize()
    {
        Runs++;
        Trace.Append(2);
    }
}

public static class Ordered
{
    public static readonly int Value = 17;
    public static int DuringInitialization;
    public static int Runs;

    static Ordered()
    {
        Runs++;
        Trace.Append(5);
        DuringInitialization = Read();
    }

    public static int Read() => Value;
}

public static class CycleA
{
    public static int Value;

    static CycleA()
    {
        Trace.Append(6);
        Value = CycleB.Value + 1;
    }
}

public static class CycleB
{
    public static int Value;

    static CycleB()
    {
        Trace.Append(7);
        Value = CycleA.Value + 1;
    }
}

public static class StoreTarget
{
    public static int Value;

    static StoreTarget()
    {
        Trace.Append(9);
        Value = 77;
    }
}
