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

    public void ReserveBytes(int count, byte fill = 0x00)
    {
        for (int i = 0; i < count; i++)
            _bytes.Add(fill);
    }

    public void RecordPatch(PatchEntry patch) => _patches.Add(patch);

    public void ApplyPatch(int offset, byte value) => _bytes[offset] = value;

    public void ApplyPatchWord(int offset, ushort value)
    {
        _bytes[offset] = (byte)(value & 0xFF);
        _bytes[offset + 1] = (byte)(value >> 8);
    }

    internal void RemoveResolvedPatches(List<int> resolvedIndices)
    {
        for (int i = resolvedIndices.Count - 1; i >= 0; i--)
            _patches.RemoveAt(resolvedIndices[i]);
    }
}
