using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public static class ScopesHandler
{
    public const int RegistersVariablesRef = 1;
    public const int HardwareVariablesRef = 2;

    public static Response Handle(Request request)
    {
        return new Response
        {
            Success = true,
            Body = new ScopesResponseBody
            {
                Scopes =
                [
                    new Scope { Name = "Registers", VariablesReference = RegistersVariablesRef, Expensive = false },
                    new Scope { Name = "Hardware", VariablesReference = HardwareVariablesRef, Expensive = false },
                ],
            },
        };
    }
}
