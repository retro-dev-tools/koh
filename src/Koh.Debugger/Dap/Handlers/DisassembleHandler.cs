using System.Text.Json;
using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

public sealed class DisassembleHandler
{
    private readonly DebugSession _session;
    public DisassembleHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        var args = request.Arguments?.Deserialize(DapJsonContext.Default.DisassembleArguments);
        if (args is null)
            return new Response { Success = false, Message = "disassemble: missing args" };

        if (_session.System is not { } gb)
            return new Response { Success = false, Message = "disassemble: no active session" };

        ushort start;
        try
        {
            string reference = args.MemoryReference;
            if (reference.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) reference = reference[2..];
            start = Convert.ToUInt16(reference, 16);
            start = (ushort)(start + args.Offset);
        }
        catch
        {
            return new Response { Success = false, Message = $"disassemble: invalid memoryReference '{args.MemoryReference}'" };
        }

        int count = Math.Max(1, Math.Min(args.InstructionCount, 512));
        var list = new List<DisassembledInstruction>(count);

        ushort pc = start;
        for (int i = 0; i < count; i++)
        {
            ushort ipc = pc;
            var (mnemonic, length) = Disassembler.DecodeOne(a => gb.Mmu.DebugRead(a), pc);
            var bytes = new byte[length];
            for (int b = 0; b < length; b++) bytes[b] = gb.Mmu.DebugRead((ushort)(pc + b));
            list.Add(new DisassembledInstruction
            {
                Address = "0x" + ipc.ToString("X4"),
                InstructionBytes = string.Join(" ", Array.ConvertAll(bytes, b => b.ToString("X2"))),
                Instruction = mnemonic,
            });
            pc = (ushort)(pc + length);
        }

        return new Response
        {
            Success = true,
            Body = new DisassembleResponseBody { Instructions = list.ToArray() },
        };
    }
}
