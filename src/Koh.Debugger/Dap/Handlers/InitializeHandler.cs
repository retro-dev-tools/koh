using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public static class InitializeHandler
{
    public static Response Handle(Request request)
    {
        return new Response
        {
            Success = true,
            Body = DapCapabilities.Phase2(),
        };
    }
}
