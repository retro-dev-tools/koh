using Silk.NET.OpenAL;

namespace Koh.Emulator.App;

/// <summary>
/// Minimal streaming OpenAL sink for the emulator's 44.1 kHz mono int16
/// PCM. A small pool of pre-allocated buffers cycles through the
/// standard three-state dance: queued → playing → processed, refilled,
/// re-queued. The APU produces ~735 samples per frame at 60 fps; four
/// ~2048-sample buffers give roughly 180 ms of headroom — enough to
/// absorb the occasional frame jitter without introducing audible lag.
///
/// <para>
/// If the source ever stops (underrun — all queued buffers played while
/// we had nothing to push), <see cref="Push"/> restarts it. That's the
/// only "error path" the sink has; OpenAL's streaming model is
/// otherwise forgiving.
/// </para>
/// </summary>
public sealed unsafe class AudioSink : IDisposable
{
    public const int SampleRate = 44_100;
    // Eight small buffers × 512 samples = 4096 samples = ~93 ms at
    // 44.1 kHz. Small buffers give the pacer tight feedback on how
    // many samples the hardware has actually consumed; too-large
    // buffers mask the drain rate and make audio-driven pacing
    // laggy (one big buffer = one binary "played" state change,
    // not a continuous depth signal).
    private const int BufferCount = 8;
    private const int SamplesPerBuffer = 512;

    // Don't start the OpenAL source until this many samples are queued.
    // Needs to cover two hazards:
    //   1. Emulator → hardware rate mismatch if we started after one
    //      buffer — drain catches up before next push, chain-restarts.
    //   2. JIT warmup: the SM83 instruction dispatch and hot loops
    //      take the first 100-200 ms of real time to compile. During
    //      that window the emulator runs below real-time, undersupply-
    //      ing the sink. If audio is already playing, that's an
    //      underrun storm.
    // Seven buffers (~80 ms) gets through JIT warmup on modest
    // machines with margin left over for normal pacing overshoot.
    private const int WarmupSamples = 7 * SamplesPerBuffer;

    private readonly ALContext _alc;
    private readonly AL _al;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly uint _source;
    private readonly Queue<uint> _freeBuffers = new();
    private readonly object _sync = new();
    // Accumulator for sub-buffer remainders. APU frames produce 738
    // samples; we need 512 per GL buffer. Rather than varying buffer
    // size (which confuses the depth math), we accumulate leftovers
    // here until we can emit another full buffer.
    private readonly short[] _staging = new short[SamplesPerBuffer];
    private int _stagingFill;
    private bool _warmedUp;

    public AudioSink()
    {
        _alc = ALContext.GetApi(soft: true);
        _al  = AL.GetApi(soft: true);

        _device = _alc.OpenDevice("");
        if (_device == null)
            throw new InvalidOperationException("alcOpenDevice returned null — no audio output available");

        _context = _alc.CreateContext(_device, null);
        if (_context == null)
            throw new InvalidOperationException("alcCreateContext failed");

        if (!_alc.MakeContextCurrent(_context))
            throw new InvalidOperationException("alcMakeContextCurrent failed");

        _source = _al.GenSource();
        _al.SetSourceProperty(_source, SourceBoolean.Looping, false);
        _al.SetSourceProperty(_source, SourceFloat.Gain, 1.0f);

        for (int i = 0; i < BufferCount; i++)
            _freeBuffers.Enqueue(_al.GenBuffer());
    }


    // Rolling counters for trace logging. Not thread-safe overall, but
    // only mutated under _sync; read without lock for reporting.
    public int Underruns { get; private set; }
    public int TotalPushes { get; private set; }
    public int TotalSamplesIn { get; private set; }

    /// <summary>
    /// Push <paramref name="samples"/> at 44.1 kHz mono int16. Returns
    /// the number of samples currently buffered by OpenAL (queued but
    /// not yet consumed by the audio hardware). The audio-driven pacer
    /// reads this value to decide whether to keep emulating, yield, or
    /// sleep until the queue drains.
    /// </summary>
    public int Push(ReadOnlySpan<short> samples)
    {
        lock (_sync)
        {
            TotalPushes++;
            TotalSamplesIn += samples.Length;
            RecycleProcessed();

            // Emit fixed-size buffers, combining the staging remainder
            // from previous pushes with the new samples. Anything that
            // doesn't fill a full buffer stays in staging for next
            // time. Keeps buffer-depth math (queued × SamplesPerBuffer)
            // honest — every queued buffer holds exactly
            // SamplesPerBuffer samples.
            int consumed = 0;
            while (_freeBuffers.Count > 0)
            {
                int remaining = SamplesPerBuffer - _stagingFill;
                int available = samples.Length - consumed;
                int toCopy = Math.Min(remaining, available);
                if (toCopy == 0) break;

                samples.Slice(consumed, toCopy).CopyTo(_staging.AsSpan(_stagingFill));
                _stagingFill += toCopy;
                consumed += toCopy;

                if (_stagingFill < SamplesPerBuffer) break;   // need more samples

                uint buf = _freeBuffers.Dequeue();
                fixed (short* p = _staging)
                    _al.BufferData(buf, BufferFormat.Mono16, p, SamplesPerBuffer * sizeof(short), SampleRate);
                _al.SourceQueueBuffers(_source, 1, &buf);
                _stagingFill = 0;
            }

            // Start the source once we've queued enough to ride out the
            // typical emulator push cadence; restart it after an actual
            // underrun. Before the warmup threshold we accept silence —
            // the cost of waiting ~46 ms for audio to begin is one-time
            // and avoids the audible machine-gun on cold start.
            _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
            if ((SourceState)state != SourceState.Playing)
            {
                _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
                int queuedSamples = queued * SamplesPerBuffer;
                if (_warmedUp)
                {
                    if (queuedSamples > 0)
                    {
                        if ((SourceState)state == SourceState.Stopped) Underruns++;
                        _al.SourcePlay(_source);
                    }
                }
                else if (queuedSamples >= WarmupSamples)
                {
                    _warmedUp = true;
                    _al.SourcePlay(_source);
                }
            }

            return BufferedSamples();
        }
    }

    /// <summary>
    /// Current number of samples queued at the sink that haven't been
    /// played yet. Used by the pacer on idle frames (no new samples to
    /// push) to decide whether to keep sleeping.
    /// </summary>
    public int Buffered()
    {
        lock (_sync)
        {
            RecycleProcessed();
            return BufferedSamples();
        }
    }

    /// <summary>
    /// Discard all queued buffers and reset the source. Called on ROM
    /// swap so the new ROM's first samples don't play over the tail of
    /// the previous ROM's queue.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _al.SourceStop(_source);
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            while (queued-- > 0)
            {
                uint buf = 0;
                _al.SourceUnqueueBuffers(_source, 1, &buf);
                if (buf != 0) _freeBuffers.Enqueue(buf);
            }
            _stagingFill = 0;
            _warmedUp = false;
        }
    }

    private void RecycleProcessed()
    {
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        while (processed-- > 0)
        {
            uint buf = 0;
            _al.SourceUnqueueBuffers(_source, 1, &buf);
            _freeBuffers.Enqueue(buf);
        }
    }

    private int BufferedSamples()
    {
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
        // SampleOffset reports position within the currently-playing
        // buffer; subtract to get "not yet consumed". Works on OpenAL
        // Soft (alGetSourcei with AL_SAMPLE_OFFSET is well-defined).
        _al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out int offsetInCurrent);
        int total = queued * SamplesPerBuffer - offsetInCurrent;
        return total < 0 ? 0 : total;
    }

    public void Dispose()
    {
        _al.SourceStop(_source);
        // Unqueue everything before deleting so OpenAL doesn't complain
        // about buffers still owned by a source.
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        for (int i = 0; i < processed; i++)
        {
            uint buf = 0;
            _al.SourceUnqueueBuffers(_source, 1, &buf);
            if (buf != 0) _al.DeleteBuffer(buf);
        }
        foreach (var buf in _freeBuffers) _al.DeleteBuffer(buf);
        _freeBuffers.Clear();
        _al.DeleteSource(_source);

        if (_context != null)
        {
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
        }
        if (_device != null) _alc.CloseDevice(_device);

        _al.Dispose();
        _alc.Dispose();
    }
}
