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

    public void Add(string file, uint line, BankedAddress address)
    {
        var key = (file, line);
        if (!_byLine.TryGetValue(key, out var list))
            _byLine[key] = list = new();
        list.Add(address);
    }

    public IReadOnlyList<BankedAddress> Lookup(string file, uint line)
    {
        return _byLine.TryGetValue((file, line), out var list)
            ? list
            : Array.Empty<BankedAddress>();
    }

    private sealed class SourceLineComparer : IEqualityComparer<(string File, uint Line)>
    {
        public static readonly SourceLineComparer Instance = new();
        public bool Equals((string File, uint Line) x, (string File, uint Line) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.File, y.File) && x.Line == y.Line;
        public int GetHashCode((string File, uint Line) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.File), obj.Line);
    }
}
