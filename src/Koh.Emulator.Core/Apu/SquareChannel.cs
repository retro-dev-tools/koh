namespace Koh.Emulator.Core.Apu;

public sealed class SquareChannel
{
    public readonly bool HasSweep;
    public readonly LengthCounter Length = new(maxLength: 64);
    public readonly VolumeEnvelope Envelope = new();
    public readonly FrequencySweep? Sweep;

    public bool Enabled;
    public int Frequency;       // 11-bit
    public int DutyStep;        // 0..7 within the 8-step duty pattern
    public int DutyPattern;     // 0..3 (12.5% / 25% / 50% / 75%)

    private int _freqCycleCounter;

    private static readonly byte[,] DutyTable =
    {
        { 0, 0, 0, 0, 0, 0, 0, 1 },  // 12.5%
        { 1, 0, 0, 0, 0, 0, 0, 1 },  // 25%
        { 1, 0, 0, 0, 0, 1, 1, 1 },  // 50%
        { 0, 1, 1, 1, 1, 1, 1, 0 },  // 75% (inverted 25% per Pan Docs)
    };

    public SquareChannel(bool hasSweep)
    {
        HasSweep = hasSweep;
        if (hasSweep) Sweep = new FrequencySweep();
    }

    public void TickT()
    {
        if (!Enabled) return;
        _freqCycleCounter--;
        if (_freqCycleCounter > 0) return;
        _freqCycleCounter = (2048 - Frequency) * 4;
        DutyStep = (DutyStep + 1) & 7;
    }

    public void TickLength() => Length.Tick(() => Enabled = false);
    public void TickEnvelope() => Envelope.Tick();
    public void TickSweep()
    {
        var newFreq = Sweep?.Tick(() => Enabled = false);
        if (newFreq is int f) Frequency = f;
    }

    public int Output()
    {
        if (!Enabled) return 0;
        byte dutyValue = DutyTable[DutyPattern, DutyStep];
        return dutyValue * Envelope.Volume;
    }

    public void Trigger(byte nrx0, byte nrx1, byte nrx2, byte nrx3, byte nrx4)
    {
        Enabled = true;
        Length.Counter = Length.MaxLength - (nrx1 & 0x3F);
        Length.Enabled = (nrx4 & 0x40) != 0;
        Frequency = ((nrx4 & 0x07) << 8) | nrx3;
        Envelope.Trigger(nrx2);
        DutyPattern = (nrx1 >> 6) & 0x03;
        _freqCycleCounter = (2048 - Frequency) * 4;
        if (HasSweep) Sweep!.Trigger(nrx0, Frequency);
    }
}
