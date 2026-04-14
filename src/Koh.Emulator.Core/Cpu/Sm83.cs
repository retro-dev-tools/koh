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

    // EI delay latch: EI sets `_eiPending = true` at its instruction boundary.
    // At the NEXT instruction boundary, that promotes to `_eiArmed = true` (so
    // the instruction after EI runs with IME still false). At the following
    // boundary, `_eiArmed` applies IME = true, and dispatch is eligible.
    private bool _eiPending;
    private bool _eiArmed;
    /// <summary>
    /// HALT bug latch per §7.4: when HALT executes while IME=0 and an interrupt
    /// is pending, HALT exits immediately but the next instruction fetch fails
    /// to increment PC (same byte executes twice).
    /// </summary>
    private bool _haltBugNextFetch;
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
                OnInstructionBoundary();
                return true;
            }
            return false;
        }

        // No pending instruction — start a new one now.
        if (Halted || Stopped)
        {
            // Wake from HALT when a pending interrupt matches the enable mask.
            bool hasPending = (_mmu.Io.Interrupts.IF & _mmu.Io.Interrupts.IE & 0x1F) != 0;
            if (Halted && hasPending)
            {
                Halted = false;
                // HALT bug: if IME was 0 when HALT woke, the next fetch does
                // not increment PC.
                if (!Ime) _haltBugNextFetch = true;
                // Fall through to ExecuteNextInstruction this tick.
            }
            else
            {
                _tCyclesRemainingInInstruction = 4;
                return false;
            }
        }

        ExecuteNextInstruction();
        return false;
    }

    private void ExecuteNextInstruction()
    {
        byte opcode = ReadImmediate();
        if (_haltBugNextFetch)
        {
            // Undo the PC increment so the next fetch re-reads the same byte.
            Registers.Pc = (ushort)(Registers.Pc - 1);
            _haltBugNextFetch = false;
        }

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
            OnInstructionBoundary();
        }
    }

    private void OnInstructionBoundary()
    {
        // Promote pending EI latch by one stage. The order matters: applying
        // _eiArmed BEFORE _eiPending advance means EI's own boundary does NOT
        // enable IME — IME becomes true one boundary later.
        if (_eiArmed) { Ime = true; _eiArmed = false; }
        if (_eiPending) { _eiArmed = true; _eiPending = false; }
        ServiceInterrupts();
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
        if (enable)
        {
            _eiPending = true;  // EI delay: IME enables after the instruction after EI
        }
        else
        {
            Ime = false;
            _eiPending = false;
            _eiArmed = false;
        }
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
        _eiPending = false;
        _eiArmed = false;
        _haltBugNextFetch = false;
        TotalTCycles = 0;
        _tCyclesRemainingInInstruction = 0;
    }
}
