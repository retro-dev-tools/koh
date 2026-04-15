using Koh.Emulator.Core.Debug;

namespace Koh.Debugger.Session;

public sealed record WatchpointInfo(string DataId, string AccessType);

public sealed class WatchpointHook : MemoryHook
{
    public readonly Dictionary<ushort, WatchpointInfo> Read = new();
    public readonly Dictionary<ushort, WatchpointInfo> Write = new();

    private readonly DebugSession _session;
    public WatchpointHook(DebugSession s) { _session = s; }

    public override void OnRead(ushort address, byte value)
    {
        if (!Read.ContainsKey(address)) return;
        _session.PauseRequested = true;
        _session.System?.RunGuard.RequestStop();
    }

    public override void OnWrite(ushort address, byte value)
    {
        if (!Write.ContainsKey(address)) return;
        _session.PauseRequested = true;
        _session.System?.RunGuard.RequestStop();
    }

    public void Clear() { Read.Clear(); Write.Clear(); }
}
