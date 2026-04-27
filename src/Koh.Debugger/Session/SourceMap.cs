using Koh.Linker.Core;

namespace Koh.Debugger.Session;

/// <summary>
/// Forward direction: <c>(file, line) → List&lt;BankedAddress&gt;</c>. Multiple
/// addresses per line are expected for macro expansions.
/// </summary>
public sealed class SourceMap
{
    private readonly Dictionary<(string File, uint Line), List<BankedAddress>> _byLine
        = new(SourceLineComparer.Instance);
    private readonly List<SourceMapEntry> _entries = [];

    public void Add(string file, uint line, BankedAddress address, byte byteCount = 1)
    {
        var key = (file, line);
        if (!_byLine.TryGetValue(key, out var list))
            _byLine[key] = list = new();
        list.Add(address);
        _entries.Add(new SourceMapEntry(file, line, address, Math.Max((byte)1, byteCount)));
    }

    public IReadOnlyList<BankedAddress> Lookup(string file, uint line)
    {
        return _byLine.TryGetValue((file, line), out var list)
            ? list
            : Array.Empty<BankedAddress>();
    }

    public SourceLocation? Lookup(BankedAddress address)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (entry.Address.Bank != address.Bank) continue;

            uint start = entry.Address.Address;
            uint end = start + entry.ByteCount;
            if (address.Address >= start && address.Address < end)
                return new SourceLocation(entry.File, entry.Line);
        }

        return null;
    }

    private readonly record struct SourceMapEntry(string File, uint Line, BankedAddress Address, byte ByteCount);

    private sealed class SourceLineComparer : IEqualityComparer<(string File, uint Line)>
    {
        public static readonly SourceLineComparer Instance = new();
        public bool Equals((string File, uint Line) x, (string File, uint Line) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.File, y.File) && x.Line == y.Line;
        public int GetHashCode((string File, uint Line) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.File), obj.Line);
    }
}

public sealed record SourceLocation(string File, uint Line);
