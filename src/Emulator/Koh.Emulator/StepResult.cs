namespace Koh.Emulator;

public readonly record struct StepResult(StopReason Reason, ulong TCyclesRan, ushort FinalPc);
