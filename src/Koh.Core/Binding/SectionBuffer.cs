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
    /// <summary>
    /// True when this section was opened with the FRAGMENT keyword.
    /// Fragment sections are allowed to appear multiple times and are concatenated.
    /// </summary>
    public bool IsFragment { get; set; }

    private readonly List<byte> _bytes = [];
    private readonly List<PatchEntry> _patches = [];

    // Mutable line-map entries. Kept mutable during emit so adjacent
    // bytes from the same source line extend the prior entry in place;
    // exposed as immutable LineMapEntry records via LineMap.
    private readonly List<LineMapSlot> _lineMap = [];

    // "Where should the next emitted byte be attributed?" — set by the
    // binder before each expanded node is emitted. Null means no line
    // info is active (pre-pass work, synthesized fills, etc.), and the
    // emit helpers skip the line map in that case.
    private string? _currentFile;
    private uint _currentLine;

    public IReadOnlyList<byte> Bytes => _bytes;
    public IReadOnlyList<PatchEntry> Patches => _patches;
    public int CurrentOffset => _bytes.Count;
    public int CurrentPC => BaseAddress + CurrentOffset;

    public IReadOnlyList<LineMapEntry> LineMap
    {
        get
        {
            var result = new LineMapEntry[_lineMap.Count];
            for (int i = 0; i < _lineMap.Count; i++)
            {
                var s = _lineMap[i];
                result[i] = new LineMapEntry(s.Offset, s.ByteCount, s.File, s.Line);
            }
            return result;
        }
    }

    public SectionBuffer(string name, SectionType type, int? fixedAddress = null, int? bank = null)
    {
        Name = name;
        Type = type;
        FixedAddress = fixedAddress;
        Bank = bank;
        BaseAddress = fixedAddress ?? 0;
    }

    /// <summary>
    /// Point "where the next emitted bytes come from" at <paramref name="file"/>:<paramref name="line"/>.
    /// Call once per expanded statement in the binder before it emits.
    /// Pass <c>null</c> for <paramref name="file"/> to disable line tracking
    /// (e.g., during synthetic DS fills that have no meaningful source).
    /// </summary>
    public void SetSourceLocation(string? file, uint line)
    {
        _currentFile = file;
        _currentLine = line;
    }

    public void EmitByte(byte value)
    {
        RecordEmission(1);
        _bytes.Add(value);
    }

    public void EmitWord(ushort value)
    {
        RecordEmission(2);
        _bytes.Add((byte)(value & 0xFF));
        _bytes.Add((byte)(value >> 8));
    }

    public void EmitLong(uint value)
    {
        RecordEmission(4);
        _bytes.Add((byte)(value & 0xFF));
        _bytes.Add((byte)((value >> 8) & 0xFF));
        _bytes.Add((byte)((value >> 16) & 0xFF));
        _bytes.Add((byte)((value >> 24) & 0xFF));
    }

    public int ReserveByte()
    {
        RecordEmission(1);
        var offset = _bytes.Count;
        _bytes.Add(0x00);
        return offset;
    }

    public int ReserveWord()
    {
        RecordEmission(2);
        var offset = _bytes.Count;
        _bytes.Add(0x00);
        _bytes.Add(0x00);
        return offset;
    }

    public int ReserveLong()
    {
        RecordEmission(4);
        var offset = _bytes.Count;
        _bytes.Add(0x00);
        _bytes.Add(0x00);
        _bytes.Add(0x00);
        _bytes.Add(0x00);
        return offset;
    }

    public void ReserveBytes(int count, byte fill = 0x00)
    {
        if (count <= 0) return;
        RecordEmission(count);
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
            // Drop line-map entries that lie past the truncation point, and
            // clip one that straddles it so the surviving portion still maps.
            for (int i = _lineMap.Count - 1; i >= 0; i--)
            {
                var slot = _lineMap[i];
                if (slot.Offset >= offset)
                {
                    _lineMap.RemoveAt(i);
                }
                else if (slot.Offset + slot.ByteCount > offset)
                {
                    slot.ByteCount = offset - slot.Offset;
                    break;
                }
            }
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

    private void RecordEmission(int byteCount)
    {
        if (_currentFile is null || byteCount <= 0) return;

        // Greedy coalesce: if the previous slot ends exactly where this
        // emission starts and has the same (file, line), extend it in
        // place. A 3-byte instruction from one source line produces one
        // slot total instead of three.
        var nextOffset = _bytes.Count;
        if (_lineMap.Count > 0)
        {
            var tail = _lineMap[_lineMap.Count - 1];
            if (tail.Offset + tail.ByteCount == nextOffset
                && tail.Line == _currentLine
                && string.Equals(tail.File, _currentFile, StringComparison.Ordinal))
            {
                tail.ByteCount += byteCount;
                return;
            }
        }
        _lineMap.Add(new LineMapSlot
        {
            Offset = nextOffset,
            ByteCount = byteCount,
            File = _currentFile,
            Line = _currentLine,
        });
    }

    private sealed class LineMapSlot
    {
        public int Offset;
        public int ByteCount;
        public required string File;
        public uint Line;
    }
}
