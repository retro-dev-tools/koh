using System.Text.Json;
using Koh.Debugger.Dap.Messages;
using Koh.Debugger.Session;

namespace Koh.Debugger.Dap.Handlers;

public sealed class SetDataBreakpointsHandler
{
    private readonly DebugSession _session;
    public SetDataBreakpointsHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.SetDataBreakpointsArguments);
        if (args is null)
            return new Response { Success = false, Message = "setDataBreakpoints: missing args" };

        _session.Watchpoints.Clear();
        var results = new List<Breakpoint>();

        foreach (var dbp in args.Breakpoints)
        {
            if (!DataBreakpointInfoHandler.TryParseAddress(dbp.DataId, out ushort address))
            {
                results.Add(new Breakpoint { Verified = false, Message = $"invalid dataId '{dbp.DataId}'" });
                continue;
            }

            string access = dbp.AccessType ?? "write";
            var info = new WatchpointInfo(dbp.DataId, access);
            if (access is "read" or "readWrite") _session.Watchpoints.Read[address] = info;
            if (access is "write" or "readWrite") _session.Watchpoints.Write[address] = info;

            results.Add(new Breakpoint { Verified = true });
        }

        return new Response
        {
            Success = true,
            Body = new SetDataBreakpointsResponseBody { Breakpoints = [.. results] },
        };
    }
}
