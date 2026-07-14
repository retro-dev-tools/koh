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
    private readonly byte[] _nr = new byte[0x30]; // $FF10..$FF3F

    // NRx4 reads return bits 6 only (length-enable) — all other bits read as 1.
    private const byte Nr14ReadMask = 0xBF;
    private const byte Nr24ReadMask = 0xBF;
    private const byte Nr34ReadMask = 0xBF;
    private const byte Nr44ReadMask = 0xBF;

    private int _sampleAccum;

    /// <summary>
    /// Set once by GameBoySystem to Timer's <c>DivApuBitHigh</c>: lets
    /// NR52's power-on handler check the DIV-APU tap bit's current level
    /// without Apu taking a hard dependency on Timer.
    /// </summary>
    public Func<bool>? DivApuBitHighProvider;

    public AudioSampleBuffer SampleBuffer { get; } = new();

    // CGB fixed both DMG-only wave-RAM obscure-behavior quirks (Pan Docs,
    // "Audio — Obscure Behavior"): see Read/Write below and
    // WaveChannel._isCgb. Set once at construction, mirroring how Ppu takes
    // its HardwareMode; GameBoySystem constructs Apu the same way it
    // constructs Ppu.
    private readonly bool _isCgb;

    public Apu(HardwareMode mode = HardwareMode.Dmg)
    {
        _isCgb = mode == HardwareMode.Cgb;
        Ch1 = new SquareChannel(hasSweep: true);
        Ch2 = new SquareChannel(hasSweep: false);
        Ch3 = new WaveChannel(_isCgb);
        Ch4 = new NoiseChannel();

        FrameSequencer.LengthClock += OnLength;
        FrameSequencer.SweepClock += OnSweepClock;
        FrameSequencer.EnvelopeClock += OnEnvelope;
    }

    // The frame sequencer is NOT clocked from here: on real hardware it is
    // clocked by the falling edge of a fixed bit of the shared Timer's
    // internal 16-bit counter (bit 12 at normal speed, bit 13 in CGB
    // double-speed) -- the same counter DIV writes reset. The caller
    // (GameBoySystem) wires `Timer.FrameSequencerFallingEdge` directly to
    // `FrameSequencer.Advance`, so the phase keeps running off DIV
    // regardless of APU power state -- powering the APU on does NOT restart
    // the phase or reset which step comes next (Blargg dmg_sound 07:
    // "Powering up APU MODs next frame time with 8192"), and a DIV write can
    // force a known phase (real hardware behavior; not something this
    // particular ROM's sync_apu/sync_sweep helpers happen to use -- they
    // synchronize by polling NR52, not by writing DIV). Only the
    // DISPATCH to Length/Sweep/Envelope is gated by Enabled (real hardware
    // doesn't clock those units while off), handled inside
    // OnLength/OnEnvelope/OnSweepClock.
    public void TickT()
    {
        if (!Enabled)
            return;

        Ch1.TickT();
        Ch2.TickT();
        Ch3.TickT();
        Ch4.TickT();

        // Fractional accumulator: emits exactly SampleHz samples per CpuHz
        // T-cycles so the stream doesn't drift against a 44100 Hz audio
        // device. Previously we emitted every 95 T-cycles = 44150.6 Hz,
        // which accumulated ~50ms of extra buffered audio per minute.
        _sampleAccum += SampleHz;
        if (_sampleAccum >= CpuHz)
        {
            _sampleAccum -= CpuHz;
            MixAndBuffer();
        }
    }

    private const int CpuHz = 4_194_304;
    private const int SampleHz = 44_100;

    private void OnLength()
    {
        if (!Enabled)
            return;
        Ch1.TickLength();
        Ch2.TickLength();
        Ch3.TickLength();
        Ch4.TickLength();
    }

    private void OnEnvelope()
    {
        if (!Enabled)
            return;
        Ch1.TickEnvelope();
        Ch2.TickEnvelope();
        Ch4.TickEnvelope();
    }

    private void OnSweepClock()
    {
        if (!Enabled)
            return;
        int? newFreq = Ch1.TickSweep(_nr[0x00]);
        if (newFreq is int f)
        {
            // Mirror the swept frequency back into the actual NR13/NR14
            // register bytes (not just the in-memory Frequency field): a
            // later trigger that doesn't rewrite NR13 must observe the
            // swept value, per Pan Docs ("this new frequency is written
            // back to the shadow register and CH1 frequency in NR13/NR14").
            _nr[0x03] = (byte)(f & 0xFF);
            _nr[0x04] = (byte)((_nr[0x04] & ~0x07) | ((f >> 8) & 0x07));
        }
    }

    private void MixAndBuffer()
    {
        short sample = (short)((Ch1.Output() + Ch2.Output() + Ch3.Output() + Ch4.Output()) * 800);
        SampleBuffer.Push(sample);
    }

    public byte Read(ushort address)
    {
        if (address >= 0xFF30 && address <= 0xFF3F)
        {
            // DMG quirk: while CH3 is active, wave RAM is only accessible to
            // the CPU during the exact T-cycle the channel itself reads it;
            // any other access returns $FF regardless of the address used.
            // CGB fixed this: access while CH3 plays reliably redirects to
            // the byte at the channel's current position, no narrow window.
            if (Ch3.Enabled)
                return _isCgb || Ch3.JustRead
                    ? Ch3.WavePattern[Ch3.CurrentBytePosition]
                    : (byte)0xFF;
            return Ch3.WavePattern[address - 0xFF30];
        }

        int idx = address - 0xFF10;
        if (idx < 0 || idx >= _nr.Length)
            return 0xFF;

        return address switch
        {
            0xFF10 => (byte)(_nr[0x00] | 0x80), // NR10: bit 7 reserved
            0xFF11 => (byte)(_nr[0x01] | 0x3F), // NR11: only duty readable
            0xFF12 => _nr[0x02], // NR12
            0xFF13 => 0xFF, // NR13 write-only
            0xFF14 => (byte)(_nr[0x04] | Nr14ReadMask),

            0xFF15 => 0xFF, // unused
            0xFF16 => (byte)(_nr[0x06] | 0x3F), // NR21
            0xFF17 => _nr[0x07], // NR22
            0xFF18 => 0xFF, // NR23 write-only
            0xFF19 => (byte)(_nr[0x09] | Nr24ReadMask),

            0xFF1A => (byte)(_nr[0x0A] | 0x7F), // NR30: bit 7 only
            0xFF1B => 0xFF, // NR31 write-only
            0xFF1C => (byte)(_nr[0x0C] | 0x9F), // NR32: bits 5-6
            0xFF1D => 0xFF, // NR33 write-only
            0xFF1E => (byte)(_nr[0x0E] | Nr34ReadMask),

            0xFF1F => 0xFF, // unused
            0xFF20 => 0xFF, // NR41 write-only
            0xFF21 => _nr[0x11], // NR42
            0xFF22 => _nr[0x12], // NR43
            0xFF23 => (byte)(_nr[0x13] | Nr44ReadMask),

            0xFF24 => _nr[0x14], // NR50
            0xFF25 => _nr[0x15], // NR51
            0xFF26 => ReadNr52(), // NR52
            _ => 0xFF,
        };
    }

    public void Write(ushort address, byte value)
    {
        if (address >= 0xFF30 && address <= 0xFF3F)
        {
            // See Read(): the same DMG narrow-access-window quirk (fixed on
            // CGB) applies to writes; outside the window the write is simply
            // dropped.
            if (Ch3.Enabled)
            {
                if (_isCgb || Ch3.JustRead)
                    Ch3.WavePattern[Ch3.CurrentBytePosition] = value;
                return;
            }
            Ch3.WavePattern[address - 0xFF30] = value;
            return;
        }

        // When APU is disabled, writes to $FF10-$FF25 are ignored EXCEPT for
        // the length-counter low bytes on DMG: NR11 / NR16 / NR1B / NR20.
        // Those still update the length counter (not the duty bits).
        if (!Enabled && address >= 0xFF10 && address <= 0xFF25 && address != 0xFF26)
        {
            switch (address)
            {
                case 0xFF11:
                    Ch1.Length.Counter = Ch1.Length.MaxLength - (value & 0x3F);
                    return;
                case 0xFF16:
                    Ch2.Length.Counter = Ch2.Length.MaxLength - (value & 0x3F);
                    return;
                case 0xFF1B:
                    Ch3.Length.Counter = Ch3.Length.MaxLength - value;
                    return;
                case 0xFF20:
                    Ch4.Length.Counter = Ch4.Length.MaxLength - (value & 0x3F);
                    return;
                default:
                    return;
            }
        }

        int idx = address - 0xFF10;
        if (idx < 0 || idx >= _nr.Length)
            return;
        _nr[idx] = value;

        switch (address)
        {
            case 0xFF10:
                // NR10 value stored above; period/direction/shift take effect
                // live (no retrigger needed) EXCEPT for one obscure case:
                // clearing the negate bit after a subtraction has already run
                // since the last trigger disables the channel immediately.
                Ch1.Sweep!.OnNr10Write(value, () => Ch1.Enabled = false);
                break;
            case 0xFF11:
                Ch1.Length.Counter = Ch1.Length.MaxLength - (value & 0x3F);
                break;
            case 0xFF12:
                Ch1.Envelope.Trigger(value);
                if ((value & 0xF8) == 0)
                    Ch1.Enabled = false; // DAC disabled
                break;
            case 0xFF13:
                Ch1.Frequency = (Ch1.Frequency & 0x700) | value;
                break;
            case 0xFF14:
            {
                Ch1.Frequency = (Ch1.Frequency & 0xFF) | ((value & 0x07) << 8);
                bool trigger = (value & 0x80) != 0;
                HandleLengthEnableWrite(
                    Ch1.Length,
                    (value & 0x40) != 0,
                    trigger,
                    () => Ch1.Enabled = false
                );
                if (trigger)
                    Ch1.Trigger(
                        _nr[0x00],
                        _nr[0x01],
                        _nr[0x02],
                        _nr[0x03],
                        value,
                        NextStepSkipsLength()
                    );
                break;
            }

            case 0xFF16:
                Ch2.Length.Counter = Ch2.Length.MaxLength - (value & 0x3F);
                break;
            case 0xFF17:
                Ch2.Envelope.Trigger(value);
                if ((value & 0xF8) == 0)
                    Ch2.Enabled = false;
                break;
            case 0xFF18:
                Ch2.Frequency = (Ch2.Frequency & 0x700) | value;
                break;
            case 0xFF19:
            {
                Ch2.Frequency = (Ch2.Frequency & 0xFF) | ((value & 0x07) << 8);
                bool trigger = (value & 0x80) != 0;
                HandleLengthEnableWrite(
                    Ch2.Length,
                    (value & 0x40) != 0,
                    trigger,
                    () => Ch2.Enabled = false
                );
                if (trigger)
                    Ch2.Trigger(0, _nr[0x06], _nr[0x07], _nr[0x08], value, NextStepSkipsLength());
                break;
            }

            case 0xFF1A:
                Ch3.DacEnabled = (value & 0x80) != 0;
                if (!Ch3.DacEnabled)
                    Ch3.Enabled = false;
                break;
            case 0xFF1B:
                Ch3.Length.Counter = Ch3.Length.MaxLength - value;
                break;
            case 0xFF1C:
                Ch3.VolumeShift = (value >> 5) & 0x03;
                break;
            case 0xFF1D:
                Ch3.Frequency = (Ch3.Frequency & 0x700) | value;
                break;
            case 0xFF1E:
            {
                Ch3.Frequency = (Ch3.Frequency & 0xFF) | ((value & 0x07) << 8);
                bool trigger = (value & 0x80) != 0;
                HandleLengthEnableWrite(
                    Ch3.Length,
                    (value & 0x40) != 0,
                    trigger,
                    () => Ch3.Enabled = false
                );
                if (trigger)
                    Ch3.Trigger(
                        _nr[0x0A],
                        _nr[0x0B],
                        _nr[0x0C],
                        _nr[0x0D],
                        value,
                        NextStepSkipsLength()
                    );
                break;
            }

            case 0xFF20:
                Ch4.Length.Counter = Ch4.Length.MaxLength - (value & 0x3F);
                break;
            case 0xFF21:
                Ch4.Envelope.Trigger(value);
                if ((value & 0xF8) == 0)
                    Ch4.Enabled = false;
                break;
            case 0xFF22:
                Ch4.ClockShift = (value >> 4) & 0x0F;
                Ch4.WidthMode = (value & 0x08) != 0;
                Ch4.DivisorCode = value & 0x07;
                break;
            case 0xFF23:
            {
                bool trigger = (value & 0x80) != 0;
                HandleLengthEnableWrite(
                    Ch4.Length,
                    (value & 0x40) != 0,
                    trigger,
                    () => Ch4.Enabled = false
                );
                if (trigger)
                    Ch4.Trigger(_nr[0x10], _nr[0x11], _nr[0x12], value, NextStepSkipsLength());
                break;
            }

            case 0xFF24: /* NR50: left/right master volume. Stored, not yet mixed. */
                break;
            case 0xFF25: /* NR51: per-channel L/R routing. Stored, not yet mixed. */
                break;
            case 0xFF26:
            {
                bool newEnabled = (value & 0x80) != 0;
                if (!newEnabled && Enabled)
                    PowerOff();
                if (newEnabled && !Enabled && DivApuBitHighProvider?.Invoke() == true)
                {
                    // "APU glitch: when turning the APU on while DIV's bit 4
                    // (bit 12 of the internal counter; bit 5/13 in CGB double
                    // speed) is on, the first DIV/APU event is skipped" (Pan
                    // Docs obscure behavior / SameBoy GB_apu_init). That
                    // pushes the next Length/Sweep/Envelope tick out by a
                    // full extra ~8192 T-cycle DIV-APU period -- dmg_sound
                    // 07 subtest 5 ("Powering up APU MODs next frame time
                    // with 8192") retriggers right after a power-on and
                    // measures exactly this.
                    FrameSequencer.SkipNext = true;
                }
                Enabled = newEnabled;
                break;
            }
        }
    }

    private byte ReadNr52()
    {
        byte status = (byte)(
            (Enabled ? 0x80 : 0)
            | 0x70
            | (Ch4.Enabled ? 0x08 : 0)
            | (Ch3.Enabled ? 0x04 : 0)
            | (Ch2.Enabled ? 0x02 : 0)
            | (Ch1.Enabled ? 0x01 : 0)
        );
        return status;
    }

    private void PowerOff()
    {
        // Clear all registers $FF10-$FF25; wave RAM is preserved on DMG.
        // Length counters themselves are NOT cleared: on DMG they keep
        // ticking/holding their value across a power cycle (mirrored by the
        // length-registers-writable-while-off quirk in Write()).
        for (int i = 0; i <= 0x15; i++)
            _nr[i] = 0;
        Ch1.Enabled = false;
        Ch1.Length.Enabled = false;
        Ch1.DutyStep = 0;
        Ch2.Enabled = false;
        Ch2.Length.Enabled = false;
        Ch2.DutyStep = 0;
        Ch3.Enabled = false;
        Ch3.Length.Enabled = false;
        Ch3.ResetOnPowerOff();
        Ch4.Enabled = false;
        Ch4.Length.Enabled = false;
        // The frame sequencer's step index does not reset on power-off, nor
        // does its DIV-driven phase (owned by Timer, not Apu): it is clocked
        // by the shared internal counter, which never stops. Only the
        // dispatch to Length/Sweep/Envelope is gated by Enabled (see
        // OnLength/OnEnvelope/OnSweepClock and TickT()).
    }

    private bool NextStepSkipsLength() => (FrameSequencer.Step & 1) == 0;

    /// <summary>
    /// Handles the obscure "extra length clocking" quirk shared by all four
    /// NRx4 registers: enabling length (0-&gt;1 transition) while the frame
    /// sequencer's NEXT tick would not clock length immediately consumes one
    /// count, if the counter is non-zero. If that reaches zero and this write
    /// isn't also a trigger, the channel is disabled right away.
    /// </summary>
    private void HandleLengthEnableWrite(
        LengthCounter length,
        bool newEnabled,
        bool triggering,
        Action disableChannel
    )
    {
        bool wasEnabled = length.Enabled;
        if (!wasEnabled && newEnabled && NextStepSkipsLength() && length.Counter > 0)
        {
            length.Counter--;
            if (length.Counter == 0 && !triggering)
                disableChannel();
        }
        length.Enabled = newEnabled;
    }

    public void WriteState(StateWriter w)
    {
        w.WriteBool(Enabled);
        w.WriteBytes(_nr);
        w.WriteBytes(Ch3.WavePattern);
        w.WriteI32(_sampleAccum);
        w.WriteI32(FrameSequencer.Step);
        w.WriteBool(FrameSequencer.SkipNext);
        // Channel live-state (enable + envelope volume) to resume audibly.
        w.WriteBool(Ch1.Enabled);
        w.WriteI32(Ch1.Envelope.Volume);
        w.WriteI32(Ch1.Frequency);
        w.WriteI32(Ch1.DutyStep);
        w.WriteI32(Ch1.DutyPattern);
        w.WriteI32(Ch1.Length.Counter);
        w.WriteBool(Ch1.Length.Enabled);
        w.WriteBool(Ch2.Enabled);
        w.WriteI32(Ch2.Envelope.Volume);
        w.WriteI32(Ch2.Frequency);
        w.WriteI32(Ch2.DutyStep);
        w.WriteI32(Ch2.DutyPattern);
        w.WriteI32(Ch2.Length.Counter);
        w.WriteBool(Ch2.Length.Enabled);
        w.WriteBool(Ch3.Enabled);
        w.WriteBool(Ch3.DacEnabled);
        w.WriteI32(Ch3.Frequency);
        w.WriteI32(Ch3.VolumeShift);
        w.WriteI32(Ch3.Length.Counter);
        w.WriteBool(Ch3.Length.Enabled);
        w.WriteBool(Ch4.Enabled);
        w.WriteI32(Ch4.Envelope.Volume);
        w.WriteI32(Ch4.ShiftRegister);
        w.WriteI32(Ch4.ClockShift);
        w.WriteBool(Ch4.WidthMode);
        w.WriteI32(Ch4.DivisorCode);
        w.WriteI32(Ch4.Length.Counter);
        w.WriteBool(Ch4.Length.Enabled);
    }

    public void ReadState(StateReader r)
    {
        Enabled = r.ReadBool();
        r.ReadBytes(_nr.AsSpan());
        r.ReadBytes(Ch3.WavePattern.AsSpan());
        _sampleAccum = r.ReadI32();
        FrameSequencer.Step = r.ReadI32();
        FrameSequencer.SkipNext = r.ReadBool();
        Ch1.Enabled = r.ReadBool();
        Ch1.Envelope.Volume = r.ReadI32();
        Ch1.Frequency = r.ReadI32();
        Ch1.DutyStep = r.ReadI32();
        Ch1.DutyPattern = r.ReadI32();
        Ch1.Length.Counter = r.ReadI32();
        Ch1.Length.Enabled = r.ReadBool();
        Ch2.Enabled = r.ReadBool();
        Ch2.Envelope.Volume = r.ReadI32();
        Ch2.Frequency = r.ReadI32();
        Ch2.DutyStep = r.ReadI32();
        Ch2.DutyPattern = r.ReadI32();
        Ch2.Length.Counter = r.ReadI32();
        Ch2.Length.Enabled = r.ReadBool();
        Ch3.Enabled = r.ReadBool();
        Ch3.DacEnabled = r.ReadBool();
        Ch3.Frequency = r.ReadI32();
        Ch3.VolumeShift = r.ReadI32();
        Ch3.Length.Counter = r.ReadI32();
        Ch3.Length.Enabled = r.ReadBool();
        Ch4.Enabled = r.ReadBool();
        Ch4.Envelope.Volume = r.ReadI32();
        Ch4.ShiftRegister = r.ReadI32();
        Ch4.ClockShift = r.ReadI32();
        Ch4.WidthMode = r.ReadBool();
        Ch4.DivisorCode = r.ReadI32();
        Ch4.Length.Counter = r.ReadI32();
        Ch4.Length.Enabled = r.ReadBool();
    }
}
