namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// CGB palette RAM: 64 bytes (8 palettes × 4 colors × 2 bytes). Access via
/// index register ($FF68 BG, $FF6A OBJ) with optional auto-increment.
/// </summary>
public sealed class CgbPalette
{
    private readonly byte[] _data = new byte[64];

    public byte IndexRegister;   // bit 7 = auto-increment, bits 0..5 = index

    public byte ReadData() => _data[IndexRegister & 0x3F];

    public void WriteData(byte value)
    {
        _data[IndexRegister & 0x3F] = value;
        if ((IndexRegister & 0x80) != 0)
        {
            byte idx = (byte)((IndexRegister & 0x3F) + 1);
            IndexRegister = (byte)((IndexRegister & 0x80) | (idx & 0x3F));
        }
    }

    /// <summary>Returns the 15-bit BGR555 color for a given palette index and color slot.</summary>
    public ushort GetColor(int paletteIndex, int colorSlot)
    {
        int offset = paletteIndex * 8 + colorSlot * 2;
        return (ushort)(_data[offset] | (_data[offset + 1] << 8));
    }

    public ReadOnlySpan<byte> RawData => _data;

    public void WriteState(State.StateWriter w) { w.WriteByte(IndexRegister); w.WriteBytes(_data); }
    public void ReadState(State.StateReader r) { IndexRegister = r.ReadByte(); r.ReadBytes(_data.AsSpan()); }
}
