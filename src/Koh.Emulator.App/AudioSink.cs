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
    private const int SampleRate = 44_100;
    // Four buffers × 2048 samples = 8192 samples = ~186 ms at 44.1 kHz.
    // Slightly larger than one emulator frame × the number of frames
    // we're willing to buffer before we start hearing latency.
    private const int BufferCount = 4;
    private const int SamplesPerBuffer = 2048;

    private readonly ALContext _alc;
    private readonly AL _al;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly uint _source;
    private readonly Queue<uint> _freeBuffers = new();
    private readonly short[] _stagingBuffer = new short[SamplesPerBuffer];

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

    /// <summary>
    /// Drain the APU sample ring into OpenAL. Returns immediately if
    /// there's nothing to push. Caller passes the number of newly
    /// available samples — the <see cref="Koh.Emulator.Core.Apu.AudioSampleBuffer"/>
    /// doesn't expose a Span drain, so we stream via <c>Drain(Span)</c>
    /// in small chunks here.
    /// </summary>
    public void Push(Koh.Emulator.Core.Apu.AudioSampleBuffer source)
    {
        // Recycle buffers OpenAL has finished with.
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        while (processed-- > 0)
        {
            uint buf = 0;
            _al.SourceUnqueueBuffers(_source, 1, &buf);
            _freeBuffers.Enqueue(buf);
        }

        // Fill + queue as many buffers as we have free AND samples to
        // cover. Stop either when we run out of free buffers (audio
        // queue is full — next frame will pick up the leftovers) or out
        // of samples.
        while (_freeBuffers.Count > 0 && source.Available >= SamplesPerBuffer)
        {
            int filled = source.Drain(_stagingBuffer);
            if (filled == 0) break;

            uint buf = _freeBuffers.Dequeue();
            fixed (short* p = _stagingBuffer)
                _al.BufferData(buf, BufferFormat.Mono16, p, filled * sizeof(short), SampleRate);
            _al.SourceQueueBuffers(_source, 1, &buf);
        }

        // If the source has stopped (underrun), kick it back to playing
        // as long as we have any queued buffers.
        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        if ((SourceState)state != SourceState.Playing)
        {
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            if (queued > 0) _al.SourcePlay(_source);
        }
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
