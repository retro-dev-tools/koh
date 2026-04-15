using System.Text.Json;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class WriteMemoryHandler
{
    private readonly DebugSession _session;
    public WriteMemoryHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.WriteMemoryArguments);
        if (args is null)
            return new Response { Success = false, Message = "writeMemory: missing args" };

        var system = _session.System;
        if (system is null)
            return new Response { Success = false, Message = "writeMemory: no active debug session" };

        if (!DataBreakpointInfoHandler.TryParseAddress(args.MemoryReference, out ushort baseAddr))
            return new Response { Success = false, Message = $"writeMemory: invalid memoryReference '{args.MemoryReference}'" };

        byte[] bytes;
        try { bytes = Convert.FromBase64String(args.Data); }
        catch (FormatException) { return new Response { Success = false, Message = "writeMemory: data is not valid base64" }; }

        int written = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            ushort addr = (ushort)(baseAddr + args.Offset + i);
            if (!system.DebugWriteByte(addr, bytes[i])) break;
            written++;
        }

        return new Response
        {
            Success = true,
            Body = new WriteMemoryResponseBody { Offset = args.Offset, BytesWritten = written },
        };
    }
}
