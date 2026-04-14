using System.Globalization;
using System.Text.Json;
using Koh.Debugger.Dap.Messages;
using Koh.Linker.Core;

namespace Koh.Debugger.Dap.Handlers;

/// <summary>
/// DAP breakpoint handlers that target raw addresses and symbols (rather than
/// source lines, which are handled by <see cref="SetBreakpointsHandler"/>).
/// </summary>
public sealed class BreakpointHandlers
{
    private readonly DebugSession _session;
    public BreakpointHandlers(DebugSession session) { _session = session; }

    public Response HandleSetInstructionBreakpoints(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.SetInstructionBreakpointsArguments);
        if (args is null)
            return new Response { Success = false, Message = "setInstructionBreakpoints: missing args" };

        // This call replaces ALL instruction breakpoints per spec. Since the
        // underlying BreakpointManager doesn't distinguish categories, we
        // maintain the fiction by clearing and re-adding — ok given our
        // current single-flat-set model.
        _session.Breakpoints.ClearAll();

        var results = new List<Breakpoint>();
        foreach (var bp in args.Breakpoints ?? Array.Empty<InstructionBreakpoint>())
        {
            if (!TryParseInstructionReference(bp.InstructionReference, out ushort addr))
            {
                results.Add(new Breakpoint { Verified = false, Message = $"invalid instructionReference '{bp.InstructionReference}'" });
                continue;
            }
            ushort finalAddr = (ushort)(addr + bp.Offset);
            byte bank = finalAddr >= 0x4000
                ? (_session.System?.Cartridge.CurrentRomBank ?? (byte)1)
                : (byte)0;
            _session.Breakpoints.Add(new BankedAddress(bank, finalAddr));
            results.Add(new Breakpoint
            {
                Verified = true,
                InstructionReference = "0x" + finalAddr.ToString("X4"),
            });
        }

        return new Response
        {
            Success = true,
            Body = new SetInstructionBreakpointsResponseBody { Breakpoints = results.ToArray() },
        };
    }

    public Response HandleSetFunctionBreakpoints(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.SetFunctionBreakpointsArguments);
        if (args is null)
            return new Response { Success = false, Message = "setFunctionBreakpoints: missing args" };

        _session.Breakpoints.ClearAll();

        var results = new List<Breakpoint>();
        foreach (var bp in args.Breakpoints ?? Array.Empty<FunctionBreakpoint>())
        {
            var sym = _session.DebugInfo.SymbolMap.Lookup(bp.Name);
            if (sym is null)
            {
                results.Add(new Breakpoint { Verified = false, Message = $"unknown symbol '{bp.Name}'" });
                continue;
            }
            _session.Breakpoints.Add(new BankedAddress(sym.Bank, sym.Address));
            results.Add(new Breakpoint
            {
                Verified = true,
                InstructionReference = "0x" + sym.Address.ToString("X4"),
            });
        }

        return new Response
        {
            Success = true,
            Body = new SetInstructionBreakpointsResponseBody { Breakpoints = results.ToArray() },
        };
    }

    public Response HandleBreakpointLocations(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.BreakpointLocationsArguments);
        if (args is null)
            return new Response { Success = false, Message = "breakpointLocations: missing args" };

        var source = args.Source?.Path ?? args.Source?.Name ?? "";
        int startLine = args.Line;
        int endLine = args.EndLine ?? args.Line;

        var locations = new List<BreakpointLocation>();
        for (int line = startLine; line <= endLine; line++)
        {
            var addrs = _session.DebugInfo.SourceMap.Lookup(source, (uint)line);
            if (addrs.Count > 0)
                locations.Add(new BreakpointLocation { Line = line });
        }

        return new Response
        {
            Success = true,
            Body = new BreakpointLocationsResponseBody { Breakpoints = locations.ToArray() },
        };
    }

    private static bool TryParseInstructionReference(string s, out ushort addr)
    {
        addr = 0;
        if (string.IsNullOrEmpty(s)) return false;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr);
        if (s.StartsWith("$"))
            return ushort.TryParse(s[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr);
        return ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr);
    }
}
