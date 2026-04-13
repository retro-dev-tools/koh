using Koh.Emulator.Core.Bus;

namespace Koh.Emulator.Core.Cpu;

/// <summary>
/// SM83 CPU using the InstructionTable decoder. Phase 2 implements the full
/// unprefixed + CB-prefixed instruction set (no exact-M-cycle memory timing
/// yet — instructions execute atomically and then sleep for their T-cycle cost).
/// </summary>
public sealed class Sm83 : InstructionTable.IInstructionBus
{
    private readonly Mmu _mmu;
    public CpuRegisters Registers;
    public Interrupts Interrupts;

    public bool Halted;
    public bool Stopped;
    public bool Ime;
    private bool _pendingImeEnable;
    public ulong TotalTCycles;

    private int _tCyclesRemainingInInstruction;

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

        if (_tCyclesRemainingInInstruction > 0)
        {
            _tCyclesRemainingInInstruction--;
            if (_tCyclesRemainingInInstruction == 0)
            {
                // Instruction boundary. Service interrupts and process EI delay.
                if (_pendingImeEnable) { Ime = true; _pendingImeEnable = false; }
                ServiceInterrupts();
                return true;
            }
            return false;
        }

        // No pending instruction — start a new one now.
        if (Halted || Stopped)
        {
            _tCyclesRemainingInInstruction = 4;
            // Wake from HALT when a pending interrupt matches the enable mask.
            if (Halted && (_mmu.Io.Interrupts.IF & _mmu.Io.Interrupts.IE & 0x1F) != 0)
                Halted = false;
            return false;
        }

        ExecuteNextInstruction();
        return false;
    }

    private void ExecuteNextInstruction()
    {
        byte opcode = ReadImmediate();
        InstructionTable.InstructionHandler? handler;
        if (opcode == 0xCB)
        {
            byte cb = ReadImmediate();
            handler = InstructionTable.CbPrefixed[cb];
        }
        else
        {
            handler = InstructionTable.Unprefixed[opcode];
        }

        if (handler is null)
        {
            Halted = true;
            _tCyclesRemainingInInstruction = 4;
            return;
        }

        int cycles = handler(ref Registers, this);
        // Decrement by one because this call consumed the first T-cycle.
        _tCyclesRemainingInInstruction = cycles - 1;
        if (_tCyclesRemainingInInstruction <= 0)
        {
            if (_pendingImeEnable) { Ime = true; _pendingImeEnable = false; }
            ServiceInterrupts();
        }
    }

    private void ServiceInterrupts()
    {
        if (!Ime) return;
        byte pending = (byte)(_mmu.Io.Interrupts.IF & _mmu.Io.Interrupts.IE & 0x1F);
        if (pending == 0) return;

        // Find lowest-set bit.
        int bit = 0;
        while ((pending & 1) == 0) { pending >>= 1; bit++; }

        _mmu.Io.Interrupts.IF &= (byte)~(1 << bit);
        Ime = false;
        Halted = false;

        // Push PC, jump to vector.
        Registers.Sp = (ushort)(Registers.Sp - 1);
        _mmu.WriteByte(Registers.Sp, (byte)(Registers.Pc >> 8));
        Registers.Sp = (ushort)(Registers.Sp - 1);
        _mmu.WriteByte(Registers.Sp, (byte)(Registers.Pc & 0xFF));
        Registers.Pc = (ushort)(0x40 + bit * 8);
        _tCyclesRemainingInInstruction = 20;
    }

    public byte ReadByte(ushort address) => _mmu.ReadByte(address);
    public void WriteByte(ushort address, byte value) => _mmu.WriteByte(address, value);

    public byte ReadImmediate()
    {
        byte value = _mmu.ReadByte(Registers.Pc);
        Registers.Pc = (ushort)(Registers.Pc + 1);
        return value;
    }

    public ushort ReadImmediate16()
    {
        byte lo = ReadImmediate();
        byte hi = ReadImmediate();
        return (ushort)((hi << 8) | lo);
    }

    public void SetIme(bool enable)
    {
        if (enable) _pendingImeEnable = true;  // EI takes effect after the next instruction
        else Ime = false;
    }

    public void Halt() => Halted = true;
    public void Stop() => Stopped = true;

    public void Reset()
    {
        Registers = default;
        Registers.Pc = 0x0100;
        Registers.Sp = 0xFFFE;
        Interrupts = default;
        Halted = false;
        Stopped = false;
        Ime = false;
        _pendingImeEnable = false;
        TotalTCycles = 0;
        _tCyclesRemainingInInstruction = 0;
    }
}
