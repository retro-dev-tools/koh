using Koh.Debugger.Session;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Debugger;

public sealed class DebugSession
{
    public GameBoySystem? System { get; private set; }
    public DebugInfoLoader DebugInfo { get; } = new();
    public BreakpointManager Breakpoints { get; } = new();

    public volatile bool PauseRequested;

    public event Action? Launched;

    public bool IsLaunched => System is not null;

    public void Launch(ReadOnlyMemory<byte> romBytes, ReadOnlyMemory<byte> kdbgBytes, HardwareMode mode)
    {
        var cart = CartridgeFactory.Load(romBytes.Span);
        System = new GameBoySystem(mode, cart);
        DebugInfo.Load(kdbgBytes);
        Launched?.Invoke();
    }

    public void Terminate()
    {
        System?.RunGuard.RequestStop();
        PauseRequested = true;
        System = null;
    }
}
