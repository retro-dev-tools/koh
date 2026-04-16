namespace Koh.Emulator.Core;

/// <summary>
/// Thread-agnostic stop flag used by the host to interrupt a long-running
/// <c>RunFrame</c> or <c>RunUntil</c>. The core checks the flag at instruction
/// boundaries; worst-case latency is the longest SM83 instruction (~24 T-cycles).
/// </summary>
public sealed class RunGuard
{
    private volatile bool _stopRequested;
    private volatile int _reason = (int)StopReason.StopRequested;

    public bool StopRequested => _stopRequested;

    public StopReason Reason => (StopReason)_reason;

    public void RequestStop(StopReason reason = StopReason.StopRequested)
    {
        _reason = (int)reason;
        _stopRequested = true;
    }

    public void Clear()
    {
        _stopRequested = false;
        _reason = (int)StopReason.StopRequested;
    }
}
