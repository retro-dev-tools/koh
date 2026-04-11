namespace Koh.Emulator.Core;

public enum StopReason
{
    FrameComplete,
    InstructionComplete,
    TCycleComplete,
    Breakpoint,
    Watchpoint,
    HaltedBySystem,
    StopRequested,
}
