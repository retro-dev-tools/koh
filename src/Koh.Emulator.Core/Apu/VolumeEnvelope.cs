namespace Koh.Emulator.Core.Apu;

public sealed class VolumeEnvelope
{
    public int Volume;
    public bool IncreaseDirection;
    public int PeriodReload;
    private int _period;

    public void Trigger(byte nrx2)
    {
        Volume = nrx2 >> 4;
        IncreaseDirection = (nrx2 & 0x08) != 0;
        PeriodReload = nrx2 & 0x07;
        _period = PeriodReload;
    }

    public void Tick()
    {
        if (PeriodReload == 0) return;
        _period--;
        if (_period > 0) return;
        _period = PeriodReload;
        if (IncreaseDirection && Volume < 15) Volume++;
        else if (!IncreaseDirection && Volume > 0) Volume--;
    }
}
