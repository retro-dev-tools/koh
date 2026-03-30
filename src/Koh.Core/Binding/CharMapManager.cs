using Koh.Core.Diagnostics;

namespace Koh.Core.Binding;

/// <summary>
/// Manages character mapping tables for string-to-byte encoding.
/// Supports multi-byte values: CHARMAP "str", $80, $00, $00.
/// </summary>
internal sealed class CharMapManager
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, Dictionary<string, byte[]>> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _mapStack = new();
    private string _activeMapName = "main";
    private Dictionary<string, byte[]> _activeMap;
    private int _maxKeyLen = 1;

    public CharMapManager(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
        _activeMap = new Dictionary<string, byte[]>();
        _maps["main"] = _activeMap;
        _maps[""] = _activeMap;
    }

    public void NewCharMap(string name, string? baseName = null)
    {
        if (_maps.ContainsKey(name))
        {
            _diagnostics.Report(default, $"Character map '{name}' already exists");
            return;
        }
        Dictionary<string, byte[]> map;
        if (baseName != null && _maps.TryGetValue(baseName, out var baseMap))
            map = new Dictionary<string, byte[]>(baseMap);
        else if (baseName != null)
        {
            _diagnostics.Report(default, $"Base character map '{baseName}' not found");
            map = new Dictionary<string, byte[]>();
        }
        else
            map = new Dictionary<string, byte[]>();

        _maps[name] = map;
        _activeMapName = name;
        _activeMap = map;
    }

    public void SetCharMap(string name)
    {
        if (!_maps.TryGetValue(name, out var map))
        {
            _diagnostics.Report(default, $"Character map '{name}' not found");
            return;
        }
        _activeMapName = name;
        _activeMap = map;
        RecalcMaxKeyLen();
    }

    private void RecalcMaxKeyLen()
    {
        _maxKeyLen = 1;
        foreach (var key in _activeMap.Keys)
            if (key.Length > _maxKeyLen) _maxKeyLen = key.Length;
    }

    public void PushCharMap() => _mapStack.Push(_activeMapName);

    public void PopCharMap()
    {
        if (_mapStack.Count == 0)
        {
            _diagnostics.Report(default, "POPC/POPCHARMAP without matching PUSHC/PRECHMAP");
            return;
        }
        SetCharMap(_mapStack.Pop());
    }

    public void AddMapping(string character, byte[] value)
    {
        _activeMap[character] = value;
        if (character.Length > _maxKeyLen)
            _maxKeyLen = character.Length;
    }

    /// <summary>
    /// REVCHAR: reverse lookup — find the string key that maps to the given byte values.
    /// </summary>
    public string? ReverseCharMap(byte[] values)
    {
        string? result = null;
        foreach (var (key, val) in _activeMap)
        {
            if (val.Length == values.Length && val.AsSpan().SequenceEqual(values))
            {
                if (result != null)
                {
                    _diagnostics.Report(default, "REVCHAR: Multiple character mappings to values");
                    return null;
                }
                result = key;
            }
        }

        if (result == null)
            _diagnostics.Report(default,
                $"REVCHAR: No character mapping to value(s) {string.Join(", ", values.Select(b => $"${b:X2}"))}");

        return result;
    }

    /// <summary>
    /// Count the number of mapped characters in a string using the active charmap.
    /// </summary>
    public int CharLen(string text)
    {
        int count = 0;
        WalkGreedyMatch(text, (_, _) => count++);
        return count;
    }

    /// <summary>Check if a string is a valid entry in the active charmap.</summary>
    public bool InCharMap(string text) => _activeMap.ContainsKey(text);

    /// <summary>
    /// Encode a string literal into bytes using the active character map.
    /// Characters not in the map use their ASCII value.
    /// </summary>
    public byte[] EncodeString(string text)
    {
        var result = new List<byte>();
        WalkGreedyMatch(text, (mapped, ch) =>
        {
            if (mapped != null)
                result.AddRange(mapped);
            else
                result.Add((byte)ch);
        });
        return result.ToArray();
    }

    /// <summary>
    /// Walk the string using greedy charmap matching. For each position, calls
    /// <paramref name="onMatch"/> with either the mapped bytes (if matched) or null and the
    /// unmapped character.
    /// </summary>
    private void WalkGreedyMatch(string text, Action<byte[]?, char> onMatch)
    {
        int i = 0;
        while (i < text.Length)
        {
            bool matched = false;
            for (int len = Math.Min(text.Length - i, _maxKeyLen); len >= 1; len--)
            {
                var substr = text.Substring(i, len);
                if (_activeMap.TryGetValue(substr, out var mapped))
                {
                    onMatch(mapped, '\0');
                    i += len;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                onMatch(null, text[i]);
                i++;
            }
        }
    }
}
