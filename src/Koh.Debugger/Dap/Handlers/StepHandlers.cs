using Koh.Debugger.Dap.Messages;
using Koh.Emulator.Core;

namespace Koh.Debugger.Dap.Handlers;

/// <summary>
/// Handlers for DAP step commands: next (step-over), stepIn, stepOut.
/// Each performs an atomic step on the emulator and returns the result,
/// after which a "stopped" event should be emitted by the caller.
/// </summary>
public sealed class StepHandlers
{
    private readonly DebugSession _session;

    public StepHandlers(DebugSession session) { _session = session; }

    public Response HandleStepIn(Request request)
    {
        if (_session.System is not { } gb)
            return new Response { Success = false, Message = "stepIn: no active session" };

        gb.StepInstruction();
        return new Response { Success = true };
    }

    public Response HandleNext(Request request)
    {
        if (_session.System is not { } gb)
            return new Response { Success = false, Message = "next: no active session" };

        // If the next instruction is a CALL or RST, step over it by running
        // until the stack returns to its current level. Otherwise step-in.
        byte opcode = gb.Mmu.DebugRead(gb.Registers.Pc);
        bool isCall = opcode == 0xCD                                  // CALL a16
                   || opcode == 0xC4 || opcode == 0xCC                // CALL cc,a16 (NZ,Z)
                   || opcode == 0xD4 || opcode == 0xDC                // CALL cc,a16 (NC,C)
                   || (opcode & 0xC7) == 0xC7;                        // RST xx ($C7,$CF,$D7,$DF,$E7,$EF,$F7,$FF)

        if (!isCall)
        {
            gb.StepInstruction();
            return new Response { Success = true };
        }

        ushort spAtCall = gb.Registers.Sp;
        // Run until SP returns to its pre-CALL value (via RET) or a safety
        // limit is hit.
        for (int budget = 0; budget < 1_000_000; budget++)
        {
            gb.StepInstruction();
            if (gb.Registers.Sp == spAtCall) break;
        }
        return new Response { Success = true };
    }

    public Response HandleStepOut(Request request)
    {
        if (_session.System is not { } gb)
            return new Response { Success = false, Message = "stepOut: no active session" };

        // Read the return address currently on top of the stack — the next RET
        // in our current function will jump there.
        ushort sp = gb.Registers.Sp;
        byte lo = gb.Mmu.DebugRead(sp);
        byte hi = gb.Mmu.DebugRead((ushort)(sp + 1));
        ushort returnPc = (ushort)((hi << 8) | lo);

        for (int budget = 0; budget < 1_000_000; budget++)
        {
            gb.StepInstruction();
            if (gb.Registers.Pc == returnPc) break;
        }
        return new Response { Success = true };
    }
}
