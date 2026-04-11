using Koh.Debugger;
using Koh.Debugger.Dap;
using Koh.Emulator.App.Services;

namespace Koh.Emulator.App.DebugMode;

public sealed class DebugModeBootstrapper
{
    public DapDispatcher Dispatcher { get; }
    public DebugSession DebugSession { get; }

    public DebugModeBootstrapper(EmulatorHost emulatorHost)
    {
        Dispatcher = new DapDispatcher();
        DebugSession = new DebugSession();

        HandlerRegistration.RegisterAll(
            Dispatcher,
            DebugSession,
            loadFile: _ => ReadOnlyMemory<byte>.Empty);
    }
}
