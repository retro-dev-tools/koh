using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Emulator.App.Services;

namespace Koh.Emulator.App.DebugMode;

public sealed class DebugModeBootstrapper
{
    public DapDispatcher Dispatcher { get; }
    public DebugSession DebugSession { get; }
    private readonly EmulatorHost _emulatorHost;

    public DebugModeBootstrapper(EmulatorHost emulatorHost, Func<string, ReadOnlyMemory<byte>> loadFile)
    {
        _emulatorHost = emulatorHost;
        Dispatcher = new DapDispatcher();
        DebugSession = new DebugSession();
        DebugSession.Launched += OnSessionLaunched;

        HandlerRegistration.RegisterAll(
            Dispatcher,
            DebugSession,
            loadFile);
    }

    private void OnSessionLaunched()
    {
        if (DebugSession.System is { } system)
        {
            _emulatorHost.AttachDebugSystem(system);
        }
    }
}
