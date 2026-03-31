namespace Koh.Core.Binding;

public enum SectionType
{
    Rom0, RomX, Vram, Sram, Wram0, WramX, Oam, Hram,
}

public sealed class SectionBuffer
{
    public string Name { get; }
    public SectionType Type { get; }
    public int? FixedAddress { get; }
    public int? Bank { get; }
    public int BaseAddress { get; }
    public int AlignBits { get; set; }
    /// <summary>
    /// Alignment offset recorded by the first ALIGN N, OFFSET directive in this section.
    /// Used by DS ALIGN to account for the section's known placement offset.
    /// </summary>
    public int AlignOffset { get; set; }

    private readonly List<byte> _bytes = [];
    private readonly List<PatchEntry> _patches = [];

    public IReadOnlyList<byte> Bytes => _bytes;
    public IReadOnlyList<PatchEntry> Patches => _patches;
    public int CurrentOffset => _bytes.Count;
    public int CurrentPC => BaseAddress + CurrentOffset;

    public SectionBuffer(string name, SectionType type, int? fixedAddress = null, int? bank = null)
    {
        Name = name;
        Type = type;
        FixedAddress = fixedAddress;
        Bank = bank;
        BaseAddress = fixedAddress ?? 0;
    }

    public void EmitByte(byte value) => _bytes.Add(value);

    public void EmitWord(ushort value)
    {
        _bytes.Add((byte)(value & 0xFF));
        _bytes.Add((byte)(value >> 8));
    }

    public void EmitLong(uint value)
    {
        _bytes.Add((byte)(value & 0xFF));
        _bytes.Add((byte)((value >> 8) & 0xFF));
        _bytes.Add((byte)((value >> 16) & 0xFF));
        _bytes.Add((byte)((value >> 24) & 0xFF));
    }

    public int ReserveByte()
    {
        var offset = _bytes.Count;
        _bytes.Add(0x00);
        return offset;
    }

    public int ReserveWord()
    {
        var offset = _bytes.Count;
        _bytes.Add(0x00);
        _bytes.Add(0x00);
        return offset;
    }

    public int ReserveLong()
    {
        var offset = _bytes.Count;
        _bytes.Add(0x00);
        _bytes.Add(0x00);
        _bytes.Add(0x00);
        _bytes.Add(0x00);
        return offset;
    }

    public void ReserveBytes(int count, byte fill = 0x00)
    {
        for (int i = 0; i < count; i++)
            _bytes.Add(fill);
    }

    /// <summary>
    /// Truncate the byte buffer to the given offset (used by UNION/NEXTU to rewind).
    /// </summary>
    internal void TruncateTo(int offset)
    {
        if (offset >= 0 && offset < _bytes.Count)
        {
            _bytes.RemoveRange(offset, _bytes.Count - offset);
            // Remove patches that reference truncated offsets
            _patches.RemoveAll(p => p.Offset >= offset);
        }
    }

    public void RecordPatch(PatchEntry patch) => _patches.Add(patch);

    public void ApplyPatch(int offset, byte value) => _bytes[offset] = value;

    public void ApplyPatchWord(int offset, ushort value)
    {
        _bytes[offset] = (byte)(value & 0xFF);
        _bytes[offset + 1] = (byte)(value >> 8);
    }

    public void ApplyPatchLong(int offset, uint value)
    {
        _bytes[offset] = (byte)(value & 0xFF);
        _bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        _bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        _bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    internal void RemoveResolvedPatches(List<int> resolvedIndices)
    {
        for (int i = resolvedIndices.Count - 1; i >= 0; i--)
            _patches.RemoveAt(resolvedIndices[i]);
    }
}
