namespace Koh.Emulator.Core.State;

public sealed class StateWriter : IDisposable
{
    private readonly BinaryWriter _w;

    public StateWriter(Stream stream)
    {
        _w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
    }

    public void WriteByte(byte v) => _w.Write(v);
    public void WriteSByte(sbyte v) => _w.Write(v);
    public void WriteU16(ushort v) => _w.Write(v);
    public void WriteI32(int v) => _w.Write(v);
    public void WriteU32(uint v) => _w.Write(v);
    public void WriteU64(ulong v) => _w.Write(v);
    public void WriteI64(long v) => _w.Write(v);
    public void WriteBool(bool v) => _w.Write(v);
    public void WriteBytes(ReadOnlySpan<byte> v) => _w.Write(v);

    public void Dispose() => _w.Dispose();
}
