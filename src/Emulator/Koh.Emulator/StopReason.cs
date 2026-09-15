namespace Koh.Emulator;

public enum StopReason
{
    FrameComplete,
    InstructionComplete,
    TCycleComplete,
    Breakpoint,
    Watchpoint,
    HaltedBySystem,
    StopRequested,
    BudgetExceeded,
    Spinning,
}
