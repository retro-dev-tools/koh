namespace Koh.Emulator.Core.State;

public sealed class StateReader : IDisposable
{
    private readonly BinaryReader _r;

    public StateReader(Stream stream)
    {
        _r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
    }

    public byte ReadByte() => _r.ReadByte();
    public sbyte ReadSByte() => _r.ReadSByte();
    public ushort ReadU16() => _r.ReadUInt16();
    public int ReadI32() => _r.ReadInt32();
    public uint ReadU32() => _r.ReadUInt32();
    public ulong ReadU64() => _r.ReadUInt64();
    public long ReadI64() => _r.ReadInt64();
    public bool ReadBool() => _r.ReadBoolean();
    public byte[] ReadBytes(int count) => _r.ReadBytes(count);
    public void ReadBytes(Span<byte> destination)
    {
        int n = _r.Read(destination);
        if (n != destination.Length) throw new EndOfStreamException();
    }

    public void Dispose() => _r.Dispose();
}
