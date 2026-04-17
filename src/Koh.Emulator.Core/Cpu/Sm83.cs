using Koh.Emulator.Core.Bus;
using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Cpu;

/// <summary>
/// SM83 CPU with M-cycle-accurate memory access timing. Each memory operation
/// (read, write, immediate fetch, internal cycle) advances peripherals by one
/// M-cycle (4 T-cycles) BEFORE the access, so reads see peripheral state as it
/// would be at that point in the real hardware's bus timing.
/// </summary>
public sealed class Sm83 : InstructionTable.IInstructionBus
{
    private readonly Mmu _mmu;
    private readonly Action _tickMCycle;
    public CpuRegisters Registers;

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

    public void WriteState(StateWriter w)
    {
        w.WriteByte(Registers.A); w.WriteByte(Registers.F);
        w.WriteByte(Registers.B); w.WriteByte(Registers.C);
        w.WriteByte(Registers.D); w.WriteByte(Registers.E);
        w.WriteByte(Registers.H); w.WriteByte(Registers.L);
        w.WriteU16(Registers.Sp); w.WriteU16(Registers.Pc);
        w.WriteBool(Halted); w.WriteBool(Stopped); w.WriteBool(Ime);
        w.WriteBool(_eiPending); w.WriteBool(_eiArmed);
        w.WriteBool(_haltBugNextFetch);
        w.WriteU64(TotalTCycles);
    }

    public void ReadState(StateReader r)
    {
        Registers.A = r.ReadByte(); Registers.F = r.ReadByte();
        Registers.B = r.ReadByte(); Registers.C = r.ReadByte();
        Registers.D = r.ReadByte(); Registers.E = r.ReadByte();
        Registers.H = r.ReadByte(); Registers.L = r.ReadByte();
        Registers.Sp = r.ReadU16(); Registers.Pc = r.ReadU16();
        Halted = r.ReadBool(); Stopped = r.ReadBool(); Ime = r.ReadBool();
        _eiPending = r.ReadBool(); _eiArmed = r.ReadBool();
        _haltBugNextFetch = r.ReadBool();
        TotalTCycles = r.ReadU64();
    }

    public Sm83(Mmu mmu) : this(mmu, () => { }) { }

    public Sm83(Mmu mmu, Action tickMCycle)
    {
        _mmu = mmu;
        _tickMCycle = tickMCycle;
        Registers.Pc = 0x0100;
        Registers.Sp = 0xFFFE;
    }

    /// <summary>
    /// Advance the CPU by one full instruction (or one idle M-cycle when
    /// halted). Peripherals tick in M-cycle chunks via the registered callback
    /// as each memory access is performed. Always returns true, since every
    /// call crosses an instruction-or-idle boundary.
    /// </summary>
    public bool TickT()
    {
        if (Halted || Stopped)
        {
            TickMCycle();  // 4 T-cycles of idle
            bool hasPending = (_mmu.Io.Interrupts.IF & _mmu.Io.Interrupts.IE & 0x1F) != 0;
            if (Halted && hasPending)
            {
                // Wake from HALT. The halt-bug only applies when HALT was
                // entered with IME=0 && pending!=0 — that path is handled in
                // Halt() and never sets Halted=true. Waking here is always a
                // clean unhalt; don't re-arm the halt-bug flag.
                Halted = false;
            }
            OnInstructionBoundary();
            return true;
        }

        ExecuteNextInstruction();
        OnInstructionBoundary();
        return true;
    }

    private void ExecuteNextInstruction()
    {
        byte opcode = ReadImmediate();
        if (_haltBugNextFetch)
        {
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
            // Unimplemented — treat as HALT to avoid runaway execution.
            Halted = true;
            return;
        }

        handler(ref Registers, this);
    }

    private void OnInstructionBoundary()
    {
        // Promote pending EI latch by one stage. Applying _eiArmed BEFORE
        // advancing _eiPending means EI's own boundary does NOT enable IME —
        // IME becomes true one boundary later.
        if (_eiArmed) { Ime = true; _eiArmed = false; }
        if (_eiPending) { _eiArmed = true; _eiPending = false; }
        ServiceInterrupts();
    }

    private void ServiceInterrupts()
    {
        if (!Ime) return;
        byte pending = (byte)(_mmu.Io.Interrupts.IF & _mmu.Io.Interrupts.IE & 0x1F);
        if (pending == 0) return;

        int bit = 0;
        byte mask = pending;
        while ((mask & 1) == 0) { mask >>= 1; bit++; }

        _mmu.Io.Interrupts.IF &= (byte)~(1 << bit);
        Ime = false;
        Halted = false;

        // Dispatch is 5 M-cycles on real hardware:
        //   2 internal, 2 stack writes, 1 PC reload.
        TickMCycle();
        TickMCycle();

        Registers.Sp = (ushort)(Registers.Sp - 1);
        TickMCycle();
        _mmu.WriteByte(Registers.Sp, (byte)(Registers.Pc >> 8));

        Registers.Sp = (ushort)(Registers.Sp - 1);
        TickMCycle();
        _mmu.WriteByte(Registers.Sp, (byte)(Registers.Pc & 0xFF));

        TickMCycle();
        Registers.Pc = (ushort)(0x40 + bit * 8);
    }

    // ─────────────────────────────────────────────────────────────
    // IInstructionBus — each method ticks peripherals by 1 M-cycle
    // (4 T-cycles) BEFORE the access.
    // ─────────────────────────────────────────────────────────────

    public byte ReadByte(ushort address)
    {
        TickMCycle();
        return _mmu.ReadByte(address);
    }

    public void WriteByte(ushort address, byte value)
    {
        // Writes commit at the END of the M-cycle. To model "3 T-cycles of
        // peripheral advance with OLD state + write + 1 T-cycle with NEW state"
        // we advance peripherals, then write; the next memory op sees the post-
        // write state 4 T-cycles later, which matches real-hardware timing for
        // all non-Timer addresses. For Timer register writes specifically, we
        // rely on the Timer's internal `_lastSelectedBit` latch to detect
        // falling edges correctly across the write.
        TickMCycle();
        _mmu.WriteByte(address, value);
    }

    public byte ReadImmediate()
    {
        TickMCycle();
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

    public void InternalCycle() => TickMCycle();

    public void SetIme(bool enable)
    {
        if (enable)
        {
            _eiPending = true;
        }
        else
        {
            Ime = false;
            _eiPending = false;
            _eiArmed = false;
        }
    }

    public void Halt()
    {
        // HALT bug: with IME=0 and a pending interrupt, the CPU does NOT halt.
        // Instead, execution continues but the next instruction fetch re-reads
        // the same byte (PC fails to increment). Matches pandocs §HALT.
        bool hasPending = (_mmu.Io.Interrupts.IF & _mmu.Io.Interrupts.IE & 0x1F) != 0;
        if (!Ime && hasPending)
        {
            _haltBugNextFetch = true;
        }
        else
        {
            Halted = true;
        }
    }
    public void Stop()
    {
        // CGB speed switch: if the game armed KEY1 (bit 0 = 1) before running
        // STOP, the instruction toggles between normal and double-speed and
        // does NOT halt the CPU. Every CGB-enhanced game uses this path
        // during boot; without it the CPU gets stuck in STOP forever.
        var keyOne = _mmu.Io.KeyOne;
        if (keyOne is { SwitchArmed: true })
        {
            keyOne.OnStopExecuted();
            // Pan Docs: STOP on real CGB also resets DIV to 0 as part of
            // the speed switch. Games that read DIV immediately afterwards
            // to calibrate timers would otherwise see stale counter data.
            // (The ~130 k T-cycle PLL relock stall is deliberately not
            // modelled — adding it risks regressing games tuned to our
            // current instant-switch timing.)
            _mmu.Io.Timer.WriteDiv();
            return;
        }
        Stopped = true;
    }

    private void TickMCycle()
    {
        TotalTCycles += 4;
        _tickMCycle();
    }

    public void Reset()
    {
        Registers = default;
        Registers.Pc = 0x0100;
        Registers.Sp = 0xFFFE;
        _mmu.Io.Interrupts = default;
        Halted = false;
        Stopped = false;
        Ime = false;
        _eiPending = false;
        _eiArmed = false;
        _haltBugNextFetch = false;
        TotalTCycles = 0;
    }
}
