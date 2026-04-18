using System.Threading;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Lock-free single-producer / single-consumer ring of <see cref="short"/>
/// samples. Capacity must be a power of two.
///
/// Overflow policy: push overwrites the oldest sample. The audio path
/// prefers brief starvation over backpressure-in-the-guest (the producer
/// is the emulator thread; we do not want to stall <c>RunFrame</c> on the
/// ring being full).
/// </summary>
public sealed class AudioRing
{
    private readonly short[] _buffer;
    private readonly int _mask;
    private int _writeIndex;
    private int _readIndex;

    public AudioRing(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException("capacity must be a positive power of two", nameof(capacity));
        _buffer = new short[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    public int Available
    {
        get
        {
            int w = Volatile.Read(ref _writeIndex);
            int r = Volatile.Read(ref _readIndex);
            int diff = w - r;
            return diff < 0 ? diff + _buffer.Length : diff;
        }
    }

    public void Push(ReadOnlySpan<short> samples)
    {
        int w = _writeIndex;
        int r = Volatile.Read(ref _readIndex);
        for (int i = 0; i < samples.Length; i++)
        {
            _buffer[w & _mask] = samples[i];
            w++;
            if ((w - r) > _buffer.Length)
            {
                r = w - _buffer.Length;
                Volatile.Write(ref _readIndex, r);
            }
        }
        Volatile.Write(ref _writeIndex, w);
    }

    public int Drain(Span<short> destination)
    {
        int w = Volatile.Read(ref _writeIndex);
        int r = _readIndex;
        int available = w - r;
        if (available < 0) available += _buffer.Length;
        int count = Math.Min(destination.Length, available);
        for (int i = 0; i < count; i++)
        {
            destination[i] = _buffer[r & _mask];
            r++;
        }
        Volatile.Write(ref _readIndex, r);
        return count;
    }
}
