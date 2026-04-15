using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Serial;

/// <summary>
/// Minimal serial-port stub. Test ROMs (Blargg) report pass/fail by writing
/// ASCII to SB and triggering the shift via SC=$81. We buffer those bytes for
/// later inspection.
/// </summary>
public sealed class Serial
{
    private readonly List<byte> _buffer = new();

    public byte SB;
    public byte SC;

    public byte ReadSB() => SB;
    public byte ReadSC() => (byte)(SC | 0x7E);

    public void WriteSB(byte value) => SB = value;

    public void WriteSC(byte value)
    {
        SC = value;
        // Start-transfer-with-internal-clock triggers a byte "send"; we capture
        // it immediately (ignoring real shift timing — sufficient for Blargg).
        if ((value & 0x81) == 0x81)
        {
            _buffer.Add(SB);
            SC = (byte)(value & 0x7F);
        }
    }

    public string ReadBufferAsString() => System.Text.Encoding.ASCII.GetString(_buffer.ToArray());
    public void ClearBuffer() => _buffer.Clear();

    public void WriteState(StateWriter w)
    {
        w.WriteByte(SB); w.WriteByte(SC);
        w.WriteI32(_buffer.Count);
        foreach (var b in _buffer) w.WriteByte(b);
    }

    public void ReadState(StateReader r)
    {
        SB = r.ReadByte(); SC = r.ReadByte();
        int n = r.ReadI32();
        _buffer.Clear();
        for (int i = 0; i < n; i++) _buffer.Add(r.ReadByte());
    }
}
