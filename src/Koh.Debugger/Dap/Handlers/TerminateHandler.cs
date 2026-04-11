using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class TerminateHandler
{
    private readonly DebugSession _session;
    public TerminateHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        _session.Terminate();
        return new Response { Success = true };
    }
}
