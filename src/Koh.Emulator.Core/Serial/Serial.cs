using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Serial;

/// <summary>
/// Serial-port with IRQ-driven shift. When SC bit 7 is set with internal clock
/// (bit 0 = 1), 8 bits are shifted out at 8192 Hz (512 T-cycles per bit). When
/// the transfer finishes SB is replaced with $FF (no linked peer) and the
/// serial interrupt fires. Blargg test ROMs trigger the shift and we capture
/// each sent byte for out-of-band reading.
/// </summary>
public sealed class Serial
{
    private readonly List<byte> _buffer = new();

    public byte SB;
    public byte SC;

    private bool _transferring;
    private int _bitsRemaining;
    private int _tCountdown;
    private byte _incomingByte = 0xFF;

    /// <summary>
    /// Optional link-cable peer. When set, internal-clock transfers exchange a
    /// byte with the peer instead of shifting in $FF from the open bus.
    /// </summary>
    public ISerialLink? Link { get; set; }

    public bool IsTransferring => _transferring;

    public byte ReadSB() => SB;
    public byte ReadSC() => (byte)(SC | 0x7E);

    public void WriteSB(byte value) => SB = value;

    public void WriteSC(byte value)
    {
        SC = value;
        if ((value & 0x81) == 0x81)
        {
            // Start transfer with internal clock. Capture the outgoing byte
            // immediately (Blargg harness relies on this), then let the shift
            // countdown play out so the interrupt fires at the correct time.
            _buffer.Add(SB);
            // If a peer is attached, exchange the byte synchronously; the peer
            // byte is what SB will hold after the shift completes.
            _incomingByte = Link?.ExchangeByte(SB) ?? 0xFF;
            _transferring = true;
            _bitsRemaining = 8;
            _tCountdown = 512;   // 512 T-cycles per bit at the DMG 8192 Hz clock
        }
    }

    public void TickT(ref Interrupts interrupts)
    {
        if (!_transferring) return;
        _tCountdown--;
        if (_tCountdown > 0) return;
        _tCountdown = 512;
        _bitsRemaining--;
        // Shift one bit of the incoming byte (from peer, or $FF if unlinked)
        // into SB's low bit. The guest sees the final exchanged byte at the
        // end of the 8-bit shift window.
        int bit = (_incomingByte >> _bitsRemaining) & 1;
        SB = (byte)((SB << 1) | bit);
        if (_bitsRemaining == 0)
        {
            _transferring = false;
            SC = (byte)(SC & 0x7F);
            interrupts.Raise(Cpu.Interrupts.Serial);
        }
    }

    public string ReadBufferAsString() => System.Text.Encoding.ASCII.GetString(_buffer.ToArray());
    public void ClearBuffer() => _buffer.Clear();

    public void WriteState(StateWriter w)
    {
        w.WriteByte(SB); w.WriteByte(SC);
        w.WriteBool(_transferring);
        w.WriteI32(_bitsRemaining);
        w.WriteI32(_tCountdown);
        w.WriteByte(_incomingByte);
        w.WriteI32(_buffer.Count);
        foreach (var b in _buffer) w.WriteByte(b);
    }

    public void ReadState(StateReader r)
    {
        SB = r.ReadByte(); SC = r.ReadByte();
        _transferring = r.ReadBool();
        _bitsRemaining = r.ReadI32();
        _tCountdown = r.ReadI32();
        _incomingByte = r.ReadByte();
        int n = r.ReadI32();
        _buffer.Clear();
        for (int i = 0; i < n; i++) _buffer.Add(r.ReadByte());
    }
}
