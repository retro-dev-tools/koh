using System.Text.Json;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class DataBreakpointInfoHandler
{
    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.DataBreakpointInfoArguments);
        if (args is null)
            return new Response { Success = false, Message = "dataBreakpointInfo: missing args" };

        // We only support watchpoints on raw memory expressions of the form
        // "$XXXX" or "0xXXXX" or decimal. Anything else (e.g. a variable name
        // resolved via variablesReference) is rejected with a null dataId.
        if (TryParseAddress(args.Name, out ushort address))
        {
            var body = new DataBreakpointInfoResponseBody
            {
                DataId = $"0x{address:X4}",
                Description = $"memory at ${address:X4}",
                CanPersist = false,
            };
            return new Response { Success = true, Body = body };
        }

        return new Response
        {
            Success = true,
            Body = new DataBreakpointInfoResponseBody
            {
                DataId = null,
                Description = "watchpoints are supported only on raw memory addresses (e.g. $C000)",
            },
        };
    }

    internal static bool TryParseAddress(string token, out ushort address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(token)) return false;
        string t = token.Trim();
        int value;
        try
        {
            if (t.StartsWith('$'))
                value = Convert.ToInt32(t[1..], 16);
            else if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = Convert.ToInt32(t[2..], 16);
            else
                value = int.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { return false; }
        if (value < 0 || value > 0xFFFF) return false;
        address = (ushort)value;
        return true;
    }
}
