using Koh.Debugger.Dap.Messages;

namespace Koh.Debugger.Dap.Handlers;

/// <summary>
/// Heuristic call-stack walker. Starts at the current PC as frame 0, then
/// walks the stack upward reading 16-bit return addresses until SP hits its
/// initial value ($FFFE on DMG boot) or the stack becomes unreadable.
/// </summary>
public sealed class StackTraceHandler
{
    private const int MaxFrames = 32;
    private const ushort StackTop = 0xFFFE;

    private readonly DebugSession _session;

    public StackTraceHandler(DebugSession session) { _session = session; }

    public Response Handle(Request request)
    {
        if (_session.System is not { } gb)
            return new Response { Success = false, Message = "stackTrace: no active session" };

        var frames = new List<StackFrame>
        {
            new() { Id = 0, Name = $"${gb.Registers.Pc:X4}", Line = 0, InstructionPointerReference = "0x" + gb.Registers.Pc.ToString("X4") },
        };

        // Walk stack: each frame's return address is a 16-bit little-endian
        // value on the stack. We can't distinguish "return address" from other
        // pushed values without dataflow analysis, so this is best-effort.
        ushort sp = gb.Registers.Sp;
        while (frames.Count < MaxFrames && sp < StackTop)
        {
            byte lo = gb.Mmu.DebugRead(sp);
            byte hi = gb.Mmu.DebugRead((ushort)(sp + 1));
            ushort retPc = (ushort)((hi << 8) | lo);
            if (retPc == 0) break;  // probably not a valid frame
            frames.Add(new StackFrame
            {
                Id = frames.Count,
                Name = $"${retPc:X4}",
                Line = 0,
                InstructionPointerReference = "0x" + retPc.ToString("X4"),
            });
            sp = (ushort)(sp + 2);
        }

        return new Response
        {
            Success = true,
            Body = new StackTraceResponseBody
            {
                StackFrames = frames.ToArray(),
                TotalFrames = frames.Count,
            },
        };
    }
}
