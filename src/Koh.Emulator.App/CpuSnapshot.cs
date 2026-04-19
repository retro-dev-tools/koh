using Koh.Emulator.Core;
using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.App;

/// <summary>
/// Immutable snapshot of the CPU's register state after a frame.
/// Published by <see cref="EmulatorLoop"/> per-frame via a volatile
/// reference write so the UI thread can read a consistent set without
/// reaching into the live <see cref="GameBoySystem"/>.
/// </summary>
public sealed record CpuSnapshot(
    ushort Pc,
    ushort Sp,
    byte A,
    byte F,
    ushort BC,
    ushort DE,
    ushort HL,
    ulong TotalTCycles,
    bool FlagZ,
    bool FlagN,
    bool FlagH,
    bool FlagC)
{
    public static CpuSnapshot From(GameBoySystem sys)
    {
        ref var r = ref sys.Cpu.Registers;
        return new CpuSnapshot(
            Pc:           r.Pc,
            Sp:           r.Sp,
            A:            r.A,
            F:            r.F,
            BC:           r.BC,
            DE:           r.DE,
            HL:           r.HL,
            TotalTCycles: sys.Cpu.TotalTCycles,
            FlagZ:        r.FlagSet(CpuRegisters.FlagZ),
            FlagN:        r.FlagSet(CpuRegisters.FlagN),
            FlagH:        r.FlagSet(CpuRegisters.FlagH),
            FlagC:        r.FlagSet(CpuRegisters.FlagC));
    }
}
