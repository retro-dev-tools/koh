using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class PauseHandler
{
    private readonly DebugSession _session;
    public PauseHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        _session.PauseRequested = true;
        _session.System?.RunGuard.RequestStop();
        return new Response { Success = true };
    }
}
