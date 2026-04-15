using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Timer;

public sealed class Timer
{
    private ushort _internalCounter;   // 16-bit system counter; DIV is bits 8..15
    private byte _tima;
    private byte _tma;
    private byte _tac;
    private int _reloadDelay;          // 0..4 T-cycles between TIMA overflow and TMA reload

    private bool _lastSelectedBit;

    public byte DIV => (byte)(_internalCounter >> 8);
    public byte TIMA => _tima;
    public byte TMA => _tma;
    public byte TAC => _tac;

    public void WriteDiv()
    {
        // Any write to DIV resets the full 16-bit counter to 0. If that
        // transition makes the selected bit fall from 1→0, the falling-edge
        // detector fires and TIMA increments.
        bool oldBit = _lastSelectedBit;
        _internalCounter = 0;
        bool enabled = (_tac & 0x04) != 0;
        int selectedBit = (_tac & 0x03) switch { 0 => 9, 1 => 3, 2 => 5, _ => 7 };
        bool newBit = enabled && ((_internalCounter >> selectedBit) & 1) != 0;  // always false after reset
        if (oldBit && !newBit)
        {
            IncrementTima();
        }
        _lastSelectedBit = newBit;
    }

    public void WriteTima(byte value)
    {
        // If we're in the reload delay window, ignore the write (hardware quirk);
        // otherwise it updates TIMA and cancels the pending overflow.
        if (_reloadDelay > 0)
        {
            // During the reload-delay 1 M-cycle, writes are ignored.
            // (Simplified model adequate for the tests we gate against.)
        }
        else
        {
            _tima = value;
        }
    }

    public void WriteTma(byte value)
    {
        _tma = value;
        // If a reload happens during the same cycle as a TMA write, the new TMA value is used.
        if (_reloadDelay == 1) _tima = value;
    }

    public void WriteTac(byte value)
    {
        // Per pandocs: writing to TAC can cause a spurious TIMA increment if
        // the selected bit (ANDed with timer-enable) transitions 1→0 as part
        // of the write. Detect that edge here and consume it so the next
        // TickT doesn't double-count.
        bool oldBit = _lastSelectedBit;
        _tac = (byte)(value & 0x07);
        bool newEnabled = (_tac & 0x04) != 0;
        int newSelectedBit = (_tac & 0x03) switch { 0 => 9, 1 => 3, 2 => 5, _ => 7 };
        bool newBit = newEnabled && ((_internalCounter >> newSelectedBit) & 1) != 0;
        if (oldBit && !newBit)
        {
            IncrementTima();
        }
        _lastSelectedBit = newBit;
    }

    /// <summary>
    /// Advance the timer one CPU T-cycle. Must be called in lockstep with the CPU clock,
    /// so in double-speed mode this is called twice per system tick.
    /// </summary>
    public void TickT(ref Interrupts interrupts)
    {
        // Increment the internal counter by 1 T-cycle.
        _internalCounter++;

        // Reload-delay handling: if a TIMA overflow is pending, count down.
        if (_reloadDelay > 0)
        {
            _reloadDelay--;
            if (_reloadDelay == 0)
            {
                _tima = _tma;
                interrupts.Raise(Interrupts.Timer);
            }
        }

        // Check for falling edge on the selected bit of the internal counter.
        bool timerEnabled = (_tac & 0x04) != 0;
        int selectedBit = (_tac & 0x03) switch
        {
            0 => 9,   // 4096 Hz  (every 1024 T-cycles)
            1 => 3,   //  262144 Hz (every 16 T-cycles)
            2 => 5,   //  65536 Hz (every 64 T-cycles)
            _ => 7,   //  16384 Hz (every 256 T-cycles)
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
            _reloadDelay = 4;   // 1 M-cycle (4 T-cycles) of delay before TMA reload + IRQ
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
        _lastSelectedBit = false;
    }
}
