using Koh.Core.Diagnostics;

namespace Koh.Core.Binding;

/// <summary>
/// Manages character mapping tables for string-to-byte encoding.
/// RGBDS directives: CHARMAP, NEWCHARMAP, SETCHARMAP, PRECHMAP, POPCHARMAP.
/// </summary>
internal sealed class CharMapManager
{
    private readonly DiagnosticBag _diagnostics;
    private readonly Dictionary<string, Dictionary<string, byte>> _maps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string> _mapStack = new();
    private string _activeMapName = "";
    private Dictionary<string, byte> _activeMap;
    private int _maxKeyLen = 1;

    public CharMapManager(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
        // Default map: identity (no custom mappings — ASCII passthrough)
        _activeMap = new Dictionary<string, byte>();
        _maps[""] = _activeMap;
    }

    /// <summary>
    /// Create a new named character map and activate it (RGBDS behavior).
    /// </summary>
    public void NewCharMap(string name)
    {
        if (_maps.ContainsKey(name))
        {
            _diagnostics.Report(default, $"Character map '{name}' already exists");
            return;
        }
        var map = new Dictionary<string, byte>();
        _maps[name] = map;
        // RGBDS: NEWCHARMAP creates and activates the new map
        _activeMapName = name;
        _activeMap = map;
    }

    /// <summary>
    /// Switch to a named character map.
    /// </summary>
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

    /// <summary>
    /// Push the current charmap name onto the stack.
    /// </summary>
    public void PushCharMap() => _mapStack.Push(_activeMapName);

    /// <summary>
    /// Pop and restore a charmap from the stack.
    /// </summary>
    public void PopCharMap()
    {
        if (_mapStack.Count == 0)
        {
            _diagnostics.Report(default, "POPC/POPCHARMAP without matching PUSHC/PRECHMAP");
            return;
        }
        SetCharMap(_mapStack.Pop());
    }

    /// <summary>
    /// Add a character mapping to the active map.
    /// </summary>
    public void AddMapping(string character, byte value)
    {
        _activeMap[character] = value;
        if (character.Length > _maxKeyLen)
            _maxKeyLen = character.Length;
    }

    /// <summary>
    /// Encode a string literal into bytes using the active character map.
    /// Characters not in the map use their ASCII value.
    /// </summary>
    public byte[] EncodeString(string text)
    {
        var result = new List<byte>();
        int i = 0;
        while (i < text.Length)
        {
            // Try longest match first (multi-char mappings)
            bool matched = false;
            for (int len = Math.Min(text.Length - i, _maxKeyLen); len >= 1; len--)
            {
                var substr = text.Substring(i, len);
                if (_activeMap.TryGetValue(substr, out var mapped))
                {
                    result.Add(mapped);
                    i += len;
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                result.Add((byte)text[i]);
                i++;
            }
        }
        return result.ToArray();
    }
}
