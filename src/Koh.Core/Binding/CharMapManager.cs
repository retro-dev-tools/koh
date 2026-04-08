using Koh.Core.Diagnostics;

namespace Koh.Core.Binding;

/// <summary>
/// Manages character mapping tables for string-to-byte encoding.
/// Supports multi-byte values: CHARMAP "str", $80, $00, $00.
/// </summary>
public sealed class CharMapManager
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

    /// <summary>
    /// Reset active charmap to "main" and clear the push/pop stack.
    /// Used before Pass 2 so charmap state is replayed from directives in order.
    /// </summary>
    public void ResetActiveState()
    {
        _mapStack.Clear();
        _activeMapName = "main";
        _activeMap = _maps["main"];
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
        if (character.Length == 0)
        {
            _diagnostics.Report(default, "CHARMAP: empty string is not allowed");
            return;
        }
        _activeMap[character] = value;
        if (character.Length > _maxKeyLen)
            _maxKeyLen = character.Length;
    }

    /// <summary>
    /// Check if the given string is a key in the active character map.
    /// </summary>
    public bool ContainsKey(string key) => _activeMap.ContainsKey(key);

    /// <summary>
    /// Count charmap-mapped characters in a string. Each longest-match charmap entry
    /// counts as one character. Unmapped characters count as one Unicode codepoint each.
    /// </summary>
    public int CharLen(string text)
    {
        int count = 0;
        int i = 0;
        while (i < text.Length)
        {
            bool matched = false;
            for (int len = Math.Min(text.Length - i, _maxKeyLen); len >= 1; len--)
            {
                var substr = text.Substring(i, len);
                if (_activeMap.TryGetValue(substr, out _))
                {
                    count++;
                    i += len;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                // Unmapped: count one Unicode codepoint (handle surrogate pairs)
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    i += 2;
                else
                    i++;
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Look up the charmap value for a single character string key.
    /// Returns the first byte of the mapped value, or null if not mapped.
    /// </summary>
    public long? LookupCharValue(string charStr)
    {
        if (_activeMap.TryGetValue(charStr, out var value) && value.Length > 0)
        {
            // Return the full integer value (reconstruct from bytes, big-endian)
            long result = 0;
            foreach (var b in value)
                result = (result << 8) | b;
            return result;
        }
        return null;
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
    /// Returns true if the exact string has a mapping in the active character map.
    /// </summary>
    public bool HasMapping(string text) =>
        text.Length > 0 && _activeMap.ContainsKey(text);

    /// <summary>Check if a string is a valid entry in the active charmap.</summary>
    public bool InCharMap(string text) => _activeMap.ContainsKey(text);

    /// <summary>
    /// Count the number of charmap-aware characters in a string.
    /// Each longest-match charmap entry counts as one character; unmapped characters count individually.
    /// </summary>
    public int CountChars(string text)
    {
        int count = 0;
        WalkLongestMatch(text, (_, _) => count++, _ => count++);
        return count;
    }


    /// <summary>
    /// Encode a string literal into bytes using the active character map.
    /// Characters not in the map use their ASCII value.
    /// </summary>
    public byte[] EncodeString(string text)
    {
        var result = new List<byte>();
        WalkLongestMatch(text,
            (_, mapped) => result.AddRange(mapped),
            ch => {
                // Emit UTF-8 bytes for unmapped characters
                var utf8 = System.Text.Encoding.UTF8.GetBytes(new[] { ch });
                result.AddRange(utf8);
            });
        return result.ToArray();
    }

    /// <summary>
    /// Walk the string using longest-match charmap lookup.
    /// Calls <paramref name="onMapped"/> for each matched charmap entry,
    /// or <paramref name="onUnmapped"/> for each unmatched character.
    /// </summary>
    private void WalkLongestMatch(string text,
        Action<string, byte[]> onMapped, Action<char> onUnmapped)
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
                    onMapped(substr, mapped);
                    i += len;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                onUnmapped(text[i]);
                i++;
            }
        }
    }
}
