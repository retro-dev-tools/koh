using Koh.Emulator.Core.Bus;

namespace Koh.Emulator.Core.Cpu;

/// <summary>
/// Phase 1 CPU: runs a single representative mock instruction per §12.9.
/// Four T-cycles per instruction. No opcode decoding yet — that arrives in Phase 3.
/// </summary>
public sealed class Sm83
{
    private readonly Mmu _mmu;
    public CpuRegisters Registers;
    public Interrupts Interrupts;

    public bool Halted;
    public ulong TotalTCycles;

    private byte _tWithinInstruction;   // 0..3

    public Sm83(Mmu mmu)
    {
        _mmu = mmu;
        Registers.Pc = 0x0100;
        Registers.Sp = 0xFFFE;
    }

    /// <summary>
    /// Advance the CPU one T-cycle. Returns true if an instruction boundary was just crossed.
    /// </summary>
    public bool TickT()
    {
        TotalTCycles++;
        _tWithinInstruction++;
        if (_tWithinInstruction >= 4)
        {
            _tWithinInstruction = 0;
            ExecuteOneMockInstruction();
            return true;
        }
        return false;
    }

    private void ExecuteOneMockInstruction()
    {
        // Representative workload per §12.9 Phase 1 row:
        //   one memory read (varying address)
        //   one ALU op
        //   one flag update
        //   one conditional branch
        byte loaded = _mmu.ReadByte(Registers.Pc);
        byte sum = (byte)(Registers.A + loaded);
        Registers.SetFlag(CpuRegisters.FlagZ, sum == 0);
        Registers.SetFlag(CpuRegisters.FlagC, sum < Registers.A);
        Registers.A = sum;

        if ((sum & 1) == 0)
            Registers.Pc = (ushort)(Registers.Pc + 1);
        else
            Registers.Pc = (ushort)(Registers.Pc + 2);
    }

    public void Reset()
    {
        Registers = default;
        Registers.Pc = 0x0100;
        Registers.Sp = 0xFFFE;
        Interrupts = default;
        Halted = false;
        TotalTCycles = 0;
        _tWithinInstruction = 0;
    }
}
