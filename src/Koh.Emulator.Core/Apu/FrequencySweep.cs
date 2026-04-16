namespace Koh.Emulator.Core.Apu;

public sealed class FrequencySweep
{
    public int ShadowFrequency;
    public int PeriodReload;
    public bool IncreaseDirection;   // false = decrease
    public int Shift;
    public bool Enabled;
    private int _period;

    public void Trigger(byte nr10, int currentFreq)
    {
        ShadowFrequency = currentFreq;
        PeriodReload = (nr10 >> 4) & 0x07;
        IncreaseDirection = (nr10 & 0x08) == 0;
        Shift = nr10 & 0x07;
        Enabled = PeriodReload != 0 || Shift != 0;
        _period = PeriodReload;
    }

    public int? Tick(Action disableChannel)
    {
        if (!Enabled) return null;
        _period--;
        if (_period > 0) return null;
        _period = PeriodReload == 0 ? 8 : PeriodReload;

        int newFreq = Calculate(disableChannel);
        if (newFreq <= 2047 && Shift > 0)
        {
            ShadowFrequency = newFreq;
            Calculate(disableChannel);
            return newFreq;
        }
        return null;
    }

    private int Calculate(Action disableChannel)
    {
        int delta = ShadowFrequency >> Shift;
        int newFreq = IncreaseDirection ? ShadowFrequency + delta : ShadowFrequency - delta;
        if (newFreq > 2047) { Enabled = false; disableChannel(); }
        return newFreq;
    }
}
