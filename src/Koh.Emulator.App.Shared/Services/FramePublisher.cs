using System.Threading;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Triple-buffered byte-frame publisher. Producer fills a "back" buffer,
/// calls <see cref="PublishBack"/> to swap it into the "published" slot
/// atomically, and gets a fresh back buffer to fill next. Consumer calls
/// <see cref="AcquireFront"/>, which returns whatever is in the
/// "published" slot, marking it as "held by UI". Subsequent publishes
/// go into the third slot, so the held buffer is never overwritten
/// mid-read.
///
/// Never blocks. Consumer may see the same frame twice if it acquires
/// faster than the producer publishes — that's intentional.
/// </summary>
public sealed class FramePublisher
{
    private readonly byte[] _a;
    private readonly byte[] _b;
    private readonly byte[] _c;

    private byte[] _published;
    private byte[] _producerBack;
    private byte[]? _consumerFront;

    private readonly Lock _gate = new();

    public int FrameBytes => _a.Length;

    public FramePublisher(int frameBytes)
    {
        _a = new byte[frameBytes];
        _b = new byte[frameBytes];
        _c = new byte[frameBytes];
        _published = _a;
        _producerBack = _b;
    }

    public byte[] AcquireBack()
    {
        return _producerBack;
    }

    public void PublishBack(byte[] buffer)
    {
        if (!ReferenceEquals(buffer, _producerBack))
            throw new InvalidOperationException("PublishBack called with a buffer that wasn't the current back");

        lock (_gate)
        {
            var oldPublished = _published;
            _published = _producerBack;

            if (_consumerFront is null)
            {
                _producerBack = oldPublished;
            }
            else
            {
                _producerBack = ThirdOf(_consumerFront, _published);
            }
        }
    }

    public byte[] AcquireFront()
    {
        lock (_gate)
        {
            if (_consumerFront is not null)
                throw new InvalidOperationException("AcquireFront without ReleaseFront");
            _consumerFront = _published;
            return _consumerFront;
        }
    }

    public void ReleaseFront(byte[] buffer)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(buffer, _consumerFront))
                throw new InvalidOperationException("ReleaseFront called with wrong buffer");
            _consumerFront = null;
        }
    }

    private byte[] ThirdOf(byte[] x, byte[] y)
    {
        if (!ReferenceEquals(x, _a) && !ReferenceEquals(y, _a)) return _a;
        if (!ReferenceEquals(x, _b) && !ReferenceEquals(y, _b)) return _b;
        return _c;
    }
}
