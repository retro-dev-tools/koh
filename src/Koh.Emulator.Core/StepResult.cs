namespace Koh.Emulator.Core;

public readonly record struct StepResult(
    StopReason Reason,
    ulong TCyclesRan,
    ushort FinalPc);
