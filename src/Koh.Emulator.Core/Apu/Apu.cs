using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Apu;

public sealed class Apu
{
    public FrameSequencer FrameSequencer { get; } = new();
    public SquareChannel Ch1 { get; }
    public SquareChannel Ch2 { get; }
    public WaveChannel Ch3 { get; }
    public NoiseChannel Ch4 { get; }

    public bool Enabled;

    // Raw NR register shadow storage. Holds last-written value so reads return
    // what was written (with the per-register reserved-bit mask applied).
    private readonly byte[] _nr = new byte[0x30];   // $FF10..$FF3F

    // NRx4 reads return bits 6 only (length-enable) — all other bits read as 1.
    private const byte Nr14ReadMask = 0xBF;
    private const byte Nr24ReadMask = 0xBF;
    private const byte Nr34ReadMask = 0xBF;
    private const byte Nr44ReadMask = 0xBF;

    private int _frameSeqCounter;
    private int _sampleCycleAccumulator;

    public AudioSampleBuffer SampleBuffer { get; } = new();

    public Apu()
    {
        Ch1 = new SquareChannel(hasSweep: true);
        Ch2 = new SquareChannel(hasSweep: false);
        Ch3 = new WaveChannel();
        Ch4 = new NoiseChannel();

        FrameSequencer.LengthClock += OnLength;
        FrameSequencer.SweepClock += () => Ch1.TickSweep();
        FrameSequencer.EnvelopeClock += OnEnvelope;
    }

    public void TickT()
    {
        if (!Enabled) return;

        _frameSeqCounter++;
        if (_frameSeqCounter >= 8192)
        {
            _frameSeqCounter = 0;
            FrameSequencer.Advance();
        }

        Ch1.TickT();
        Ch2.TickT();
        Ch3.TickT();
        Ch4.TickT();

        _sampleCycleAccumulator++;
        if (_sampleCycleAccumulator >= 95)
        {
            _sampleCycleAccumulator = 0;
            MixAndBuffer();
        }
    }

    private void OnLength()
    {
        Ch1.TickLength();
        Ch2.TickLength();
        Ch3.TickLength();
        Ch4.TickLength();
    }

    private void OnEnvelope()
    {
        Ch1.TickEnvelope();
        Ch2.TickEnvelope();
        Ch4.TickEnvelope();
    }

    private void MixAndBuffer()
    {
        short sample = (short)((Ch1.Output() + Ch2.Output() + Ch3.Output() + Ch4.Output()) * 800);
        SampleBuffer.Push(sample);
    }

    public byte Read(ushort address)
    {
        if (address >= 0xFF30 && address <= 0xFF3F)
            return Ch3.WavePattern[address - 0xFF30];

        int idx = address - 0xFF10;
        if (idx < 0 || idx >= _nr.Length) return 0xFF;

        return address switch
        {
            0xFF10 => (byte)(_nr[0x00] | 0x80),                       // NR10: bit 7 reserved
            0xFF11 => (byte)(_nr[0x01] | 0x3F),                       // NR11: only duty readable
            0xFF12 => _nr[0x02],                                      // NR12
            0xFF13 => 0xFF,                                           // NR13 write-only
            0xFF14 => (byte)(_nr[0x04] | Nr14ReadMask),

            0xFF15 => 0xFF,                                           // unused
            0xFF16 => (byte)(_nr[0x06] | 0x3F),                       // NR21
            0xFF17 => _nr[0x07],                                      // NR22
            0xFF18 => 0xFF,                                           // NR23 write-only
            0xFF19 => (byte)(_nr[0x09] | Nr24ReadMask),

            0xFF1A => (byte)(_nr[0x0A] | 0x7F),                       // NR30: bit 7 only
            0xFF1B => 0xFF,                                           // NR31 write-only
            0xFF1C => (byte)(_nr[0x0C] | 0x9F),                       // NR32: bits 5-6
            0xFF1D => 0xFF,                                           // NR33 write-only
            0xFF1E => (byte)(_nr[0x0E] | Nr34ReadMask),

            0xFF1F => 0xFF,                                           // unused
            0xFF20 => 0xFF,                                           // NR41 write-only
            0xFF21 => _nr[0x11],                                      // NR42
            0xFF22 => _nr[0x12],                                      // NR43
            0xFF23 => (byte)(_nr[0x13] | Nr44ReadMask),

            0xFF24 => _nr[0x14],                                      // NR50
            0xFF25 => _nr[0x15],                                      // NR51
            0xFF26 => ReadNr52(),                                     // NR52
            _ => 0xFF,
        };
    }

    public void Write(ushort address, byte value)
    {
        if (address >= 0xFF30 && address <= 0xFF3F)
        {
            Ch3.WavePattern[address - 0xFF30] = value;
            return;
        }

        // When APU is disabled, writes to $FF10-$FF25 are ignored. NR52 + wave
        // RAM remain writable. (Length regs $FF11/$FF16/$FF1B/$FF20 have a
        // DMG-specific carve-out that Blargg tests; implemented conservatively.)
        if (!Enabled && address >= 0xFF10 && address <= 0xFF25 && address != 0xFF26)
            return;

        int idx = address - 0xFF10;
        if (idx < 0 || idx >= _nr.Length) return;
        _nr[idx] = value;

        switch (address)
        {
            case 0xFF10: Ch1.Sweep?.Trigger(value, Ch1.Frequency); break;
            case 0xFF11: Ch1.Length.Counter = Ch1.Length.MaxLength - (value & 0x3F); break;
            case 0xFF12:
                Ch1.Envelope.Trigger(value);
                if ((value & 0xF8) == 0) Ch1.Enabled = false;   // DAC disabled
                break;
            case 0xFF13: Ch1.Frequency = (Ch1.Frequency & 0x700) | value; break;
            case 0xFF14:
                Ch1.Frequency = (Ch1.Frequency & 0xFF) | ((value & 0x07) << 8);
                Ch1.Length.Enabled = (value & 0x40) != 0;
                if ((value & 0x80) != 0)
                    Ch1.Trigger(_nr[0x00], _nr[0x01], _nr[0x02], _nr[0x03], value);
                break;

            case 0xFF16: Ch2.Length.Counter = Ch2.Length.MaxLength - (value & 0x3F); break;
            case 0xFF17:
                Ch2.Envelope.Trigger(value);
                if ((value & 0xF8) == 0) Ch2.Enabled = false;
                break;
            case 0xFF18: Ch2.Frequency = (Ch2.Frequency & 0x700) | value; break;
            case 0xFF19:
                Ch2.Frequency = (Ch2.Frequency & 0xFF) | ((value & 0x07) << 8);
                Ch2.Length.Enabled = (value & 0x40) != 0;
                if ((value & 0x80) != 0)
                    Ch2.Trigger(0, _nr[0x06], _nr[0x07], _nr[0x08], value);
                break;

            case 0xFF1A:
                Ch3.DacEnabled = (value & 0x80) != 0;
                if (!Ch3.DacEnabled) Ch3.Enabled = false;
                break;
            case 0xFF1B: Ch3.Length.Counter = Ch3.Length.MaxLength - value; break;
            case 0xFF1C: Ch3.VolumeShift = (value >> 5) & 0x03; break;
            case 0xFF1D: Ch3.Frequency = (Ch3.Frequency & 0x700) | value; break;
            case 0xFF1E:
                Ch3.Frequency = (Ch3.Frequency & 0xFF) | ((value & 0x07) << 8);
                Ch3.Length.Enabled = (value & 0x40) != 0;
                if ((value & 0x80) != 0)
                    Ch3.Trigger(_nr[0x0A], _nr[0x0B], _nr[0x0C], _nr[0x0D], value);
                break;

            case 0xFF20: Ch4.Length.Counter = Ch4.Length.MaxLength - (value & 0x3F); break;
            case 0xFF21:
                Ch4.Envelope.Trigger(value);
                if ((value & 0xF8) == 0) Ch4.Enabled = false;
                break;
            case 0xFF22:
                Ch4.ClockShift = (value >> 4) & 0x0F;
                Ch4.WidthMode = (value & 0x08) != 0;
                Ch4.DivisorCode = value & 0x07;
                break;
            case 0xFF23:
                Ch4.Length.Enabled = (value & 0x40) != 0;
                if ((value & 0x80) != 0)
                    Ch4.Trigger(_nr[0x10], _nr[0x11], _nr[0x12], value);
                break;

            case 0xFF24: /* NR50: left/right master volume. Stored, not yet mixed. */ break;
            case 0xFF25: /* NR51: per-channel L/R routing. Stored, not yet mixed. */ break;
            case 0xFF26:
                {
                    bool newEnabled = (value & 0x80) != 0;
                    if (!newEnabled && Enabled) PowerOff();
                    Enabled = newEnabled;
                    break;
                }
        }
    }

    private byte ReadNr52()
    {
        byte status = (byte)((Enabled ? 0x80 : 0) | 0x70
            | (Ch4.Enabled ? 0x08 : 0)
            | (Ch3.Enabled ? 0x04 : 0)
            | (Ch2.Enabled ? 0x02 : 0)
            | (Ch1.Enabled ? 0x01 : 0));
        return status;
    }

    private void PowerOff()
    {
        // Clear all registers $FF10-$FF25; wave RAM is preserved on DMG.
        for (int i = 0; i <= 0x15; i++) _nr[i] = 0;
        Ch1.Enabled = false;
        Ch2.Enabled = false;
        Ch3.Enabled = false;
        Ch4.Enabled = false;
        FrameSequencer.Reset();
    }

    public void WriteState(StateWriter w)
    {
        w.WriteBool(Enabled);
        w.WriteBytes(_nr);
        w.WriteBytes(Ch3.WavePattern);
        w.WriteI32(_frameSeqCounter);
        w.WriteI32(_sampleCycleAccumulator);
        w.WriteI32(FrameSequencer.Step);
        // Channel live-state (enable + envelope volume) to resume audibly.
        w.WriteBool(Ch1.Enabled); w.WriteI32(Ch1.Envelope.Volume); w.WriteI32(Ch1.Frequency); w.WriteI32(Ch1.DutyStep); w.WriteI32(Ch1.DutyPattern); w.WriteI32(Ch1.Length.Counter); w.WriteBool(Ch1.Length.Enabled);
        w.WriteBool(Ch2.Enabled); w.WriteI32(Ch2.Envelope.Volume); w.WriteI32(Ch2.Frequency); w.WriteI32(Ch2.DutyStep); w.WriteI32(Ch2.DutyPattern); w.WriteI32(Ch2.Length.Counter); w.WriteBool(Ch2.Length.Enabled);
        w.WriteBool(Ch3.Enabled); w.WriteBool(Ch3.DacEnabled); w.WriteI32(Ch3.Frequency); w.WriteI32(Ch3.VolumeShift); w.WriteI32(Ch3.Length.Counter); w.WriteBool(Ch3.Length.Enabled);
        w.WriteBool(Ch4.Enabled); w.WriteI32(Ch4.Envelope.Volume); w.WriteI32(Ch4.ShiftRegister); w.WriteI32(Ch4.ClockShift); w.WriteBool(Ch4.WidthMode); w.WriteI32(Ch4.DivisorCode); w.WriteI32(Ch4.Length.Counter); w.WriteBool(Ch4.Length.Enabled);
    }

    public void ReadState(StateReader r)
    {
        Enabled = r.ReadBool();
        r.ReadBytes(_nr.AsSpan());
        r.ReadBytes(Ch3.WavePattern.AsSpan());
        _frameSeqCounter = r.ReadI32();
        _sampleCycleAccumulator = r.ReadI32();
        FrameSequencer.Step = r.ReadI32();
        Ch1.Enabled = r.ReadBool(); Ch1.Envelope.Volume = r.ReadI32(); Ch1.Frequency = r.ReadI32(); Ch1.DutyStep = r.ReadI32(); Ch1.DutyPattern = r.ReadI32(); Ch1.Length.Counter = r.ReadI32(); Ch1.Length.Enabled = r.ReadBool();
        Ch2.Enabled = r.ReadBool(); Ch2.Envelope.Volume = r.ReadI32(); Ch2.Frequency = r.ReadI32(); Ch2.DutyStep = r.ReadI32(); Ch2.DutyPattern = r.ReadI32(); Ch2.Length.Counter = r.ReadI32(); Ch2.Length.Enabled = r.ReadBool();
        Ch3.Enabled = r.ReadBool(); Ch3.DacEnabled = r.ReadBool(); Ch3.Frequency = r.ReadI32(); Ch3.VolumeShift = r.ReadI32(); Ch3.Length.Counter = r.ReadI32(); Ch3.Length.Enabled = r.ReadBool();
        Ch4.Enabled = r.ReadBool(); Ch4.Envelope.Volume = r.ReadI32(); Ch4.ShiftRegister = r.ReadI32(); Ch4.ClockShift = r.ReadI32(); Ch4.WidthMode = r.ReadBool(); Ch4.DivisorCode = r.ReadI32(); Ch4.Length.Counter = r.ReadI32(); Ch4.Length.Enabled = r.ReadBool();
    }
}
