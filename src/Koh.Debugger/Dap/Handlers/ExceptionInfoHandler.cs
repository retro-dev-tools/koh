using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public static class ExceptionInfoHandler
{
    public static Response Handle(Request request) => new() { Success = true, Body = new { } };
}
