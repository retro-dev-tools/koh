namespace Koh.Emulator.Core.Apu;

public sealed class SquareChannel
{
    public readonly bool HasSweep;
    public readonly LengthCounter Length = new(maxLength: 64);
    public readonly VolumeEnvelope Envelope = new();
    public readonly FrequencySweep? Sweep;

    public bool Enabled;
    public int Frequency; // 11-bit
    public int DutyStep; // 0..7 within the 8-step duty pattern
    public int DutyPattern; // 0..3 (12.5% / 25% / 50% / 75%)

    private int _freqCycleCounter;

    private static readonly byte[,] DutyTable =
    {
        { 0, 0, 0, 0, 0, 0, 0, 1 }, // 12.5%
        { 1, 0, 0, 0, 0, 0, 0, 1 }, // 25%
        { 1, 0, 0, 0, 0, 1, 1, 1 }, // 50%
        { 0, 1, 1, 1, 1, 1, 1, 0 }, // 75% (inverted 25% per Pan Docs)
    };

    public SquareChannel(bool hasSweep)
    {
        HasSweep = hasSweep;
        if (hasSweep)
            Sweep = new FrequencySweep();
    }

    public void TickT()
    {
        if (!Enabled)
            return;
        _freqCycleCounter--;
        if (_freqCycleCounter > 0)
            return;
        _freqCycleCounter = (2048 - Frequency) * 4;
        DutyStep = (DutyStep + 1) & 7;
    }

    public void TickLength() => Length.Tick(() => Enabled = false);

    public void TickEnvelope() => Envelope.Tick();

    /// <summary>
    /// Clocked by the frame sequencer's 128 Hz sweep event. <paramref name="nr10"/>
    /// is the CURRENT (live) NR10 byte: period/shift/direction are re-read
    /// from it every time the sweep timer fires, not cached from trigger.
    /// Returns the new frequency when the sweep wrote one back, so the
    /// caller can also mirror it into the NR13/NR14 register storage (a
    /// later trigger with no NR13 rewrite must see the swept frequency, not
    /// the original written value — Blargg dmg_sound 05: "Subtract mode uses
    /// two's complement").
    /// </summary>
    public int? TickSweep(byte nr10)
    {
        var newFreq = Sweep?.Tick(nr10, () => Enabled = false);
        if (newFreq is int f)
            Frequency = f;
        return newFreq;
    }

    public int Output()
    {
        if (!Enabled)
            return 0;
        byte dutyValue = DutyTable[DutyPattern, DutyStep];
        return dutyValue * Envelope.Volume;
    }

    public void Trigger(
        byte nrx0,
        byte nrx1,
        byte nrx2,
        byte nrx3,
        byte nrx4,
        bool lengthSkipsNext = false
    )
    {
        Enabled = true;
        Length.Enabled = (nrx4 & 0x40) != 0;
        // Trigger only reloads the length counter when it has run out; a
        // running counter is left untouched (Blargg dmg_sound 02: "Trigger
        // shouldn't affect length"). If the reload coincides with a frame
        // sequencer step that won't clock length next, the obscure extra-clock
        // quirk immediately consumes one count (63/255 instead of 64/256).
        if (Length.Counter == 0)
        {
            Length.Counter = Length.MaxLength;
            if (Length.Enabled && lengthSkipsNext)
                Length.Counter--;
        }
        Frequency = ((nrx4 & 0x07) << 8) | nrx3;
        Envelope.Trigger(nrx2);
        DutyPattern = (nrx1 >> 6) & 0x03;
        _freqCycleCounter = (2048 - Frequency) * 4;
        if (HasSweep)
            Sweep!.Trigger(nrx0, Frequency, () => Enabled = false);
        // DAC disabled (NRx2 bits 3..7 all zero) → trigger immediately disables
        // the channel. Per pandocs APU channel DAC behaviour.
        if ((nrx2 & 0xF8) == 0)
            Enabled = false;
    }
}
