namespace Koh.Emulator.Core.Apu;

public sealed class AudioSampleBuffer
{
    private const int Capacity = 8192;
    private readonly short[] _buffer = new short[Capacity];
    private int _writeIndex;
    private int _readIndex;

    public int Available
    {
        get
        {
            int diff = _writeIndex - _readIndex;
            return diff < 0 ? diff + Capacity : diff;
        }
    }

    public void Push(short sample)
    {
        _buffer[_writeIndex] = sample;
        _writeIndex = (_writeIndex + 1) % Capacity;
        if (_writeIndex == _readIndex)
            _readIndex = (_readIndex + 1) % Capacity;  // overflow: drop oldest
    }

    public int Drain(Span<short> destination)
    {
        int count = Math.Min(destination.Length, Available);
        for (int i = 0; i < count; i++)
        {
            destination[i] = _buffer[_readIndex];
            _readIndex = (_readIndex + 1) % Capacity;
        }
        return count;
    }
}
