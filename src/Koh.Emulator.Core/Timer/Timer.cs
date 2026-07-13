using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Timer;

public sealed class Timer
{
    private ushort _internalCounter; // 16-bit system counter; DIV is bits 8..15
    private byte _tima;
    private byte _tma;
    private byte _tac;
    private int _reloadDelay; // 0..4 T-cycles between TIMA overflow and TMA reload

    // True only for the instant right after the TMA reload has just committed
    // (set at the T-cycle _reloadDelay reaches 0, cleared on the very next
    // TickT). A TIMA/TMA write observed while this is set lands on the exact
    // reload cycle: the reload wins over a TIMA write, and a TMA write is
    // also propagated into TIMA (the reload circuit copies TMA "live").
    private bool _justReloaded;

    private bool _lastSelectedBit;

    // DIV-APU falling-edge tracking: the APU frame sequencer is clocked by the
    // falling edge of a fixed internal-counter bit (bit 12 at normal speed,
    // bit 13 in CGB double-speed — pandocs "DIV-APU"), completely independent
    // of TAC/TIMA. This lives in Timer (not Apu) because it is the SAME
    // 16-bit counter a DIV write resets, so a DIV write can force a known
    // frame-sequencer phase exactly as it can glitch TIMA.
    private bool _lastDivApuBit;

    public event Action? FrameSequencerFallingEdge;

    public byte DIV => (byte)(_internalCounter >> 8);
    public byte TIMA => _tima;
    public byte TMA => _tma;
    public byte TAC => _tac;

    private static int DivApuBit(bool doubleSpeed) => doubleSpeed ? 13 : 12;

    public void WriteDiv()
    {
        // Any write to DIV resets the full 16-bit counter to 0. If that
        // transition makes the selected bit fall from 1→0, the falling-edge
        // detector fires and TIMA increments. The same reset can also fall
        // the DIV-APU bit (it becomes 0 unconditionally after the reset, so
        // only the OLD value matters — no need to know the current CPU
        // speed here), clocking the frame sequencer one extra step. This is
        // the real-hardware mechanism real games (and test ROMs) can use to
        // force a known frame-sequencer phase via a DIV write.
        bool oldBit = _lastSelectedBit;
        bool oldDivApuBit = _lastDivApuBit;
        _internalCounter = 0;
        bool enabled = (_tac & 0x04) != 0;
        int selectedBit = (_tac & 0x03) switch
        {
            0 => 9,
            1 => 3,
            2 => 5,
            _ => 7,
        };
        bool newBit = enabled && ((_internalCounter >> selectedBit) & 1) != 0; // always false after reset
        if (oldBit && !newBit)
        {
            IncrementTima();
        }
        _lastSelectedBit = newBit;

        if (oldDivApuBit)
        {
            FrameSequencerFallingEdge?.Invoke();
        }
        _lastDivApuBit = false; // always false after reset
    }

    public void WriteTima(byte value)
    {
        if (_justReloaded)
        {
            // Exact reload cycle: the TMA reload that just committed wins;
            // this write is dropped entirely.
            return;
        }
        if (_reloadDelay > 0)
        {
            // Still inside the post-overflow delay window: writing TIMA
            // cancels the pending reload and interrupt outright.
            _reloadDelay = 0;
        }
        _tima = value;
    }

    public void WriteTma(byte value)
    {
        _tma = value;
        // A TMA write on the exact reload cycle is also copied into TIMA
        // (the reload logic reads TMA "live" at that instant).
        if (_justReloaded)
            _tima = value;
    }

    public void WriteTac(byte value)
    {
        // Per pandocs: writing to TAC can cause a spurious TIMA increment if
        // the selected bit (ANDed with timer-enable) transitions 1→0 as part
        // of the write. Detect that edge here and consume it so the next
        // TickT doesn't double-count.
        bool oldBit = _lastSelectedBit;
        bool oldEnabled = (_tac & 0x04) != 0;
        int oldSelectedBit = SelectedBit(_tac);
        _tac = (byte)(value & 0x07);
        bool newEnabled = (_tac & 0x04) != 0;
        int newSelectedBit = SelectedBit(_tac);
        bool newBit = newEnabled && ((_internalCounter >> newSelectedBit) & 1) != 0;
        if (oldBit && !newBit)
        {
            IncrementTima();
        }
        else if (
            newEnabled
            && _internalCounter != 0
            && FellOnLastTick(newSelectedBit)
            && !(oldEnabled && FellOnLastTick(oldSelectedBit))
        )
        {
            // MMIO writes in this core commit at the END of the M-cycle (the
            // CPU ticks 4 T-cycles, then performs the access), one T-cycle
            // later than they land on hardware. If the newly selected bit fell
            // on the very last counter tick, hardware was already running with
            // the new TAC value for that tick and counted the falling edge —
            // count it retroactively (Mooneye: acceptance/timer/rapid_toggle).
            IncrementTima();
        }
        _lastSelectedBit = newBit;
    }

    private static int SelectedBit(byte tac) =>
        (tac & 0x03) switch
        {
            0 => 9,
            1 => 3,
            2 => 5,
            _ => 7,
        };

    /// <summary>True when the given counter bit had a falling edge on the most recent tick.</summary>
    private bool FellOnLastTick(int bit) =>
        ((_internalCounter >> bit) & 1) == 0 && (((_internalCounter - 1) >> bit) & 1) != 0;

    /// <summary>
    /// Advance the timer one CPU T-cycle. Must be called in lockstep with the CPU clock,
    /// so in double-speed mode this is called twice per system tick.
    /// </summary>
    /// <param name="doubleSpeed">
    /// CGB double-speed mode: the DIV-APU frame-sequencer tap moves from bit
    /// 12 to bit 13 of the internal counter, since the counter itself now
    /// advances twice as fast per unit of wall-clock time (it still advances
    /// once per T-cycle here, same as TAC/TIMA — see the M-cycle loop in
    /// GameBoySystem).
    /// </param>
    public void TickT(ref Interrupts interrupts, bool doubleSpeed = false)
    {
        // Increment the internal counter by 1 T-cycle.
        _internalCounter++;

        int divApuBit = DivApuBit(doubleSpeed);
        bool currentDivApuBit = ((_internalCounter >> divApuBit) & 1) != 0;
        if (_lastDivApuBit && !currentDivApuBit)
        {
            FrameSequencerFallingEdge?.Invoke();
        }
        _lastDivApuBit = currentDivApuBit;

        // The "exact reload cycle" window is exactly the 1 T-cycle right
        // after the reload commits; it does not survive into this tick.
        _justReloaded = false;

        // Reload-delay handling: if a TIMA overflow is pending, count down.
        if (_reloadDelay > 0)
        {
            _reloadDelay--;
            if (_reloadDelay == 0)
            {
                _tima = _tma;
                interrupts.Raise(Interrupts.Timer);
                _justReloaded = true;
            }
        }

        // Check for falling edge on the selected bit of the internal counter.
        bool timerEnabled = (_tac & 0x04) != 0;
        int selectedBit = (_tac & 0x03) switch
        {
            0 => 9, // 4096 Hz  (every 1024 T-cycles)
            1 => 3, //  262144 Hz (every 16 T-cycles)
            2 => 5, //  65536 Hz (every 64 T-cycles)
            _ => 7, //  16384 Hz (every 256 T-cycles)
        };
        bool currentBit = timerEnabled && ((_internalCounter >> selectedBit) & 1) != 0;

        if (_lastSelectedBit && !currentBit)
        {
            IncrementTima();
        }
        _lastSelectedBit = currentBit;
    }

    private void IncrementTima()
    {
        if (_tima == 0xFF)
        {
            _tima = 0;
            _reloadDelay = 4; // 1 M-cycle (4 T-cycles) of delay before TMA reload + IRQ
        }
        else
        {
            _tima++;
        }
    }

    public void Reset()
    {
        _internalCounter = 0;
        _tima = 0;
        _tma = 0;
        _tac = 0;
        _reloadDelay = 0;
        _justReloaded = false;
        _lastSelectedBit = false;
        _lastDivApuBit = false;
    }

    public void WriteState(StateWriter w)
    {
        w.WriteU16(_internalCounter);
        w.WriteByte(_tima);
        w.WriteByte(_tma);
        w.WriteByte(_tac);
        w.WriteI32(_reloadDelay);
        w.WriteBool(_justReloaded);
        w.WriteBool(_lastSelectedBit);
        w.WriteBool(_lastDivApuBit);
    }

    public void ReadState(StateReader r)
    {
        _internalCounter = r.ReadU16();
        _tima = r.ReadByte();
        _tma = r.ReadByte();
        _tac = r.ReadByte();
        _reloadDelay = r.ReadI32();
        _justReloaded = r.ReadBool();
        _lastSelectedBit = r.ReadBool();
        _lastDivApuBit = r.ReadBool();
    }
}
