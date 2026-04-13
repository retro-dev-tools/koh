using System.Text.Json;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class ReadMemoryHandler
{
    private readonly DebugSession _session;
    public ReadMemoryHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.ReadMemoryArguments);
        if (args is null)
            return new Response { Success = false, Message = "readMemory: missing args" };

        var system = _session.System;
        if (system is null)
            return new Response { Success = false, Message = "readMemory: no active session" };

        ushort start;
        try
        {
            string reference = args.MemoryReference;
            if (reference.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                reference = reference[2..];
            start = Convert.ToUInt16(reference, 16);
            start = (ushort)(start + args.Offset);
        }
        catch
        {
            return new Response { Success = false, Message = $"readMemory: invalid memoryReference '{args.MemoryReference}'" };
        }

        int count = Math.Max(0, Math.Min(args.Count, 0x10000 - start));
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
            bytes[i] = system.DebugReadByte((ushort)(start + i));

        return new Response
        {
            Success = true,
            Body = new ReadMemoryResponseBody
            {
                Address = "0x" + start.ToString("X4"),
                UnreadableBytes = 0,
                Data = Convert.ToBase64String(bytes),
            },
        };
    }
}
