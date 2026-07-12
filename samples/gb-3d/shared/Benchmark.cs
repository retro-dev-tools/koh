static class Benchmark
{
    internal static ushort CompletedFrames;
    internal static ushort DisplayRefreshes;
    internal static ushort ComputeTicks;
    internal static ushort TransferTicks;
    internal static ushort MissedDeadlines;
    internal static byte RendererId;
    internal static byte ValidationFaults;

    internal static void Begin(byte rendererId)
    {
        RendererId = rendererId;
        CompletedFrames = 0;
        DisplayRefreshes = 0;
        ComputeTicks = 0;
        TransferTicks = 0;
        MissedDeadlines = 0;
        ValidationFaults = 0;
    }

    internal static void CompleteFrame()
    {
        CompletedFrames++;
    }

    internal static void Refresh()
    {
        DisplayRefreshes++;
    }

    internal static void AddTransfer(byte ticks)
    {
        TransferTicks += ticks;
    }

    internal static void MissDeadline()
    {
        MissedDeadlines++;
    }
}
