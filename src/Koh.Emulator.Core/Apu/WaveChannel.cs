namespace Koh.Emulator.Core.Apu;

public sealed class WaveChannel
{
    public readonly LengthCounter Length = new(maxLength: 256);
    public bool DacEnabled;
    public bool Enabled;
    public int Frequency;
    public int VolumeShift;    // 0 = mute, 1 = 100%, 2 = 50%, 3 = 25%
    public readonly byte[] WavePattern = new byte[16];  // $FF30-$FF3F, 32 4-bit samples

    private int _waveIndex;
    private int _freqCycleCounter;

    public void TickT()
    {
        if (!Enabled) return;
        _freqCycleCounter--;
        if (_freqCycleCounter > 0) return;
        _freqCycleCounter = (2048 - Frequency) * 2;
        _waveIndex = (_waveIndex + 1) & 31;
    }

    public void TickLength() => Length.Tick(() => Enabled = false);

    public int Output()
    {
        if (!Enabled || !DacEnabled || VolumeShift == 0) return 0;
        int sampleByte = WavePattern[_waveIndex / 2];
        int sample = (_waveIndex & 1) == 0 ? (sampleByte >> 4) : (sampleByte & 0x0F);
        return sample >> (VolumeShift - 1);
    }

    public void Trigger(byte nr30, byte nr31, byte nr32, byte nr33, byte nr34)
    {
        DacEnabled = (nr30 & 0x80) != 0;
        Length.Counter = Length.MaxLength - nr31;
        Length.Enabled = (nr34 & 0x40) != 0;
        VolumeShift = (nr32 >> 5) & 0x03;
        Frequency = ((nr34 & 0x07) << 8) | nr33;
        _waveIndex = 0;
        _freqCycleCounter = (2048 - Frequency) * 2;
        Enabled = DacEnabled;
    }
}
