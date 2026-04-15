namespace Koh.Emulator.Core;

/// <summary>
/// Thread-agnostic stop flag used by the host to interrupt a long-running
/// <c>RunFrame</c> or <c>RunUntil</c>. The core checks the flag at instruction
/// boundaries; worst-case latency is the longest SM83 instruction (~24 T-cycles).
/// </summary>
public sealed class RunGuard
{
    private volatile bool _stopRequested;

    public bool StopRequested => _stopRequested;

    public void RequestStop() => _stopRequested = true;

    public void Clear() => _stopRequested = false;
}
