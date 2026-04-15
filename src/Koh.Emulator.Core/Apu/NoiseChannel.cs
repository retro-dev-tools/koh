namespace Koh.Emulator.Core.Apu;

public sealed class NoiseChannel
{
    public readonly LengthCounter Length = new(maxLength: 64);
    public readonly VolumeEnvelope Envelope = new();
    public bool Enabled;
    public int ShiftRegister = 0x7FFF;
    public int ClockShift;
    public bool WidthMode;    // 7-bit or 15-bit LFSR
    public int DivisorCode;

    private int _freqCycleCounter;

    private static readonly int[] Divisors = { 8, 16, 32, 48, 64, 80, 96, 112 };

    public void TickT()
    {
        if (!Enabled) return;
        _freqCycleCounter--;
        if (_freqCycleCounter > 0) return;
        _freqCycleCounter = Divisors[DivisorCode] << ClockShift;

        int bit0 = ShiftRegister & 1;
        int bit1 = (ShiftRegister >> 1) & 1;
        int xor = bit0 ^ bit1;
        ShiftRegister = (ShiftRegister >> 1) | (xor << 14);
        if (WidthMode)
        {
            ShiftRegister = (ShiftRegister & ~(1 << 6)) | (xor << 6);
        }
    }

    public void TickLength() => Length.Tick(() => Enabled = false);
    public void TickEnvelope() => Envelope.Tick();

    public int Output()
    {
        if (!Enabled) return 0;
        return (~ShiftRegister & 1) * Envelope.Volume;
    }

    public void Trigger(byte nr41, byte nr42, byte nr43, byte nr44)
    {
        Length.Counter = Length.MaxLength - (nr41 & 0x3F);
        Length.Enabled = (nr44 & 0x40) != 0;
        Envelope.Trigger(nr42);
        ClockShift = (nr43 >> 4) & 0x0F;
        WidthMode = (nr43 & 0x08) != 0;
        DivisorCode = nr43 & 0x07;
        ShiftRegister = 0x7FFF;
        _freqCycleCounter = Divisors[DivisorCode] << ClockShift;
        Enabled = (nr42 & 0xF8) != 0;   // DAC disabled → channel off on trigger
    }
}
