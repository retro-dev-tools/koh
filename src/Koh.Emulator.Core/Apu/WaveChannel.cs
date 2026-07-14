namespace Koh.Emulator.Core.Apu;

public sealed class WaveChannel
{
    public readonly LengthCounter Length = new(maxLength: 256);
    public bool DacEnabled;
    public bool Enabled;
    public int Frequency;
    public int VolumeShift; // 0 = mute, 1 = 100%, 2 = 50%, 3 = 25%
    public readonly byte[] WavePattern = new byte[16]; // $FF30-$FF3F, 32 4-bit samples

    // CGB fixed the DMG-only retrigger-corruption quirk (Pan Docs, "Audio —
    // Obscure Behavior"): retriggering while the channel is mid-read no
    // longer corrupts wave RAM. Set once at construction, mirroring how
    // Ppu takes its HardwareMode.
    private readonly bool _isCgb;

    private int _waveIndex;
    private int _freqCycleCounter;

    // The channel doesn't emit the sample it just fetched: it emits whatever
    // was last latched into its internal buffer, which is only refreshed when
    // the frequency timer fires. (Re)triggering resets the position but does
    // NOT refill the buffer, so playback of the new position is delayed by one
    // sample period (Pan Docs "playback delay" / "access order").
    private int _sampleBuffer;

    // Nonzero only during the exact T-cycle the channel itself latches a
    // wave RAM sample; while nonzero, wave RAM is accessible to the CPU. On
    // DMG, wave RAM is only accessible "on the same cycle that CH3 does"
    // (Pan Docs, Sound Controller "Obscure Behavior"/wave access notes) --
    // a single-T-cycle pulse, not a multi-cycle window. Sm83.ReadByte/
    // WriteByte tick peripherals for the whole M-cycle (4 T-cycles) BEFORE
    // committing the CPU's access, so the CPU's read only ever observes the
    // state as of the LAST T-cycle of its own M-cycle; a width-1 pulse set
    // the instant the fetch happens and cleared by the very next TickT call
    // reproduces "same cycle" exactly -- it survives to the CPU's access
    // only when the fetch itself happened on that M-cycle's final T-cycle.
    // (A wider window, tried previously, is wrong: for periods <= 4 T-cycles
    // -- reachable via NR33/NR34, e.g. dmg_sound 09/10/12's period-4 case --
    // a width-4 window never closes, so JustRead was permanently true and
    // FF30-FF3F reads never saw the "locked out" $FF the ROMs check for.)
    private int _justReadCountdown;

    public int CurrentBytePosition => _waveIndex / 2;
    public bool JustRead => _justReadCountdown > 0;

    public WaveChannel(bool isCgb = false)
    {
        _isCgb = isCgb;
    }

    /// <summary>
    /// Powering the APU off clears the internal sample buffer (CH3 emits
    /// "digital 0" right after power-on) and its position, per Pan Docs.
    /// </summary>
    public void ResetOnPowerOff()
    {
        _waveIndex = 0;
        _sampleBuffer = 0;
        _justReadCountdown = 0;
    }

    public void TickT()
    {
        if (_justReadCountdown > 0)
            _justReadCountdown--;
        if (!Enabled)
            return;
        _freqCycleCounter--;
        if (_freqCycleCounter > 0)
            return;
        _freqCycleCounter = (2048 - Frequency) * 2;
        _waveIndex = (_waveIndex + 1) & 31;
        int sampleByte = WavePattern[_waveIndex / 2];
        _sampleBuffer = (_waveIndex & 1) == 0 ? (sampleByte >> 4) : (sampleByte & 0x0F);
        _justReadCountdown = 1;
    }

    public void TickLength() => Length.Tick(() => Enabled = false);

    public int Output()
    {
        if (!Enabled || !DacEnabled || VolumeShift == 0)
            return 0;
        return _sampleBuffer >> (VolumeShift - 1);
    }

    public void Trigger(
        byte nr30,
        byte nr31,
        byte nr32,
        byte nr33,
        byte nr34,
        bool lengthSkipsNext = false
    )
    {
        DacEnabled = (nr30 & 0x80) != 0;
        Length.Enabled = (nr34 & 0x40) != 0;
        if (Length.Counter == 0)
        {
            Length.Counter = Length.MaxLength;
            if (Length.Enabled && lengthSkipsNext)
                Length.Counter--;
        }
        VolumeShift = (nr32 >> 5) & 0x03;
        Frequency = ((nr34 & 0x07) << 8) | nr33;

        // DMG-only quirk: retriggering while the channel is actively reading
        // a wave RAM byte corrupts the first four bytes. CGB fixed this --
        // no corruption on retrigger.
        if (!_isCgb && Enabled && JustRead)
            CorruptWaveRam();

        _waveIndex = 0; // position resets; the sample buffer is NOT refilled.
        _freqCycleCounter = (2048 - Frequency) * 2;
        Enabled = DacEnabled;
    }

    private void CorruptWaveRam()
    {
        int bytePos = _waveIndex / 2;
        if (bytePos < 4)
        {
            WavePattern[0] = WavePattern[bytePos];
        }
        else
        {
            int aligned = bytePos & ~3;
            WavePattern[0] = WavePattern[aligned];
            WavePattern[1] = WavePattern[aligned + 1];
            WavePattern[2] = WavePattern[aligned + 2];
            WavePattern[3] = WavePattern[aligned + 3];
        }
    }
}
