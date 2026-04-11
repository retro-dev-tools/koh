using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class ContinueHandler
{
    private readonly DebugSession _session;
    public ContinueHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        _session.PauseRequested = false;
        return new Response
        {
            Success = true,
            Body = new ContinueResponseBody { AllThreadsContinued = true },
        };
    }
}
