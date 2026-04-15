using System.Text.Json;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class SetBreakpointsHandler
{
    private readonly DebugSession _session;
    public SetBreakpointsHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.SetBreakpointsArguments);
        if (args is null)
            return new Response { Success = false, Message = "setBreakpoints: missing args" };

        // Phase 1: breakpoints are stored but never halt execution (advertising set
        // excludes actual halt behavior). We still resolve source locations against
        // .kdbg and return verified results to VS Code so the gutter shows a red marker.

        _session.Breakpoints.ClearAll();

        var source = args.Source.Path ?? args.Source.Name ?? "";
        var results = new List<Breakpoint>();

        foreach (var bp in args.Breakpoints ?? [])
        {
            var addresses = _session.DebugInfo.SourceMap.Lookup(source, (uint)bp.Line);
            bool verified = addresses.Count > 0;
            if (verified)
            {
                int hitTarget = ParseHitCondition(bp.HitCondition);
                foreach (var addr in addresses)
                    _session.Breakpoints.Add(addr, bp.Condition, hitTarget);
            }
            results.Add(new Breakpoint
            {
                Verified = verified,
                Line = bp.Line,
                Source = args.Source,
                Message = verified ? null : "no code at this line",
            });
        }

        return new Response
        {
            Success = true,
            Body = new SetBreakpointsResponseBody { Breakpoints = [.. results] },
        };
    }

    private static int ParseHitCondition(string? hit)
    {
        if (string.IsNullOrWhiteSpace(hit)) return 0;
        // VS Code sends either a bare number ("5") or e.g. ">=5". Accept a bare
        // integer; treat unrecognized formats as "always break".
        var t = hit.Trim();
        int i = 0;
        while (i < t.Length && !char.IsDigit(t[i])) i++;
        return int.TryParse(t.AsSpan(i), out var n) ? n : 0;
    }
}
