namespace Koh.Core.Binding;

/// <summary>
/// Lightweight PC tracking for Pass 1 — no byte arrays, just counters.
/// </summary>
internal sealed class SectionPCTracker
{
    private readonly Dictionary<string, int> _sectionPCs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sectionBasePCs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _sectionAlignBits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SectionType> _sectionTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string?> _sectionStack = new();
    private string? _activeSection;

    // Union state — stack for nested UNION support
    private readonly record struct UnionState(int StartPC, int MaxPC);
    private readonly Stack<UnionState> _unionStack = new();

    // LOAD state: labels go to load section, PC tracking continues in enclosing section
    private string? _loadSection;

    public int CurrentPC => _activeSection != null && _sectionPCs.TryGetValue(_activeSection, out var pc)
        ? pc : 0;

    public string? ActiveSectionName => _activeSection;

    public bool HasActiveSection => _activeSection != null;

    /// <summary>Known alignment bits for the active section (0 = no alignment info).</summary>
    public int ActiveAlignBits => _activeSection != null && _sectionAlignBits.TryGetValue(_activeSection, out var bits)
        ? bits : 0;

    public void SetActive(string sectionName, int basePC, SectionType? type = null)
    {
        _activeSection = sectionName;
        _sectionPCs.TryAdd(sectionName, basePC);
        _sectionBasePCs.TryAdd(sectionName, basePC);
        _sectionAlignBits.TryAdd(sectionName, 0);
        if (type.HasValue)
            _sectionTypes[sectionName] = type.Value;
    }

    /// <summary>Get the section type for the active section, or null if not known.</summary>
    public SectionType? ActiveSectionType => _activeSection != null && _sectionTypes.TryGetValue(_activeSection, out var t) ? t : null;

    /// <summary>Current byte offset from section start (PC minus base PC).</summary>
    public int ActiveSectionOffset => _activeSection != null
        && _sectionBasePCs.TryGetValue(_activeSection, out var basePC)
        ? CurrentPC - basePC : CurrentPC;

    /// <summary>Returns the maximum byte capacity for the given section type.</summary>
    public static int MaxSizeForType(SectionType type) => type switch
    {
        SectionType.Rom0  => 0x4000,
        SectionType.RomX  => 0x4000,
        SectionType.Wram0 => 0x1000,
        SectionType.WramX => 0x1000,
        SectionType.Vram  => 0x2000,
        SectionType.Hram  => 0x7F,
        SectionType.Oam   => 0xA0,
        SectionType.Sram  => 0x2000,
        _ => int.MaxValue,
    };

    /// <summary>Record that the active section has at least the given alignment.</summary>
    public void SetAlignBits(int bits)
    {
        if (_activeSection != null && bits > ActiveAlignBits)
            _sectionAlignBits[_activeSection] = bits;
    }

    public void Advance(int bytes)
    {
        if (_activeSection != null)
            _sectionPCs[_activeSection] += bytes;
    }

    public void PushSection()
    {
        _sectionStack.Push(_activeSection);
        _activeSection = null; // PUSHS clears active section
    }

    public bool PopSection()
    {
        if (_sectionStack.Count == 0) return false;
        _activeSection = _sectionStack.Pop();
        return true;
    }

    // --- Union support ---

    public void BeginUnion()
    {
        if (_activeSection == null) return;
        _unionStack.Push(new UnionState(CurrentPC, CurrentPC));
    }

    public bool NextUnion()
    {
        if (_unionStack.Count == 0) return false;
        var state = _unionStack.Pop();
        int maxPC = Math.Max(state.MaxPC, CurrentPC);
        if (_activeSection != null)
            _sectionPCs[_activeSection] = state.StartPC;
        _unionStack.Push(new UnionState(state.StartPC, maxPC));
        return true;
    }

    public bool EndUnion()
    {
        if (_unionStack.Count == 0) return false;
        var state = _unionStack.Pop();
        int maxPC = Math.Max(state.MaxPC, CurrentPC);
        if (_activeSection != null)
            _sectionPCs[_activeSection] = maxPC;
        return true;
    }

    // --- LOAD support ---

    /// <summary>Section name used for label addresses (switches during LOAD).</summary>
    public string? LabelSectionName => _loadSection ?? _activeSection;

    /// <summary>PC for label placement (uses load section PC if in a LOAD block).</summary>
    public int LabelPC => _loadSection != null && _sectionPCs.TryGetValue(_loadSection, out var pc)
        ? pc : CurrentPC;

    public void BeginLoad(string loadSectionName, int basePC)
    {
        if (_loadSection != null) return; // nested LOAD not supported
        _loadSection = loadSectionName;
        _sectionPCs.TryAdd(loadSectionName, basePC);
    }

    public bool EndLoad()
    {
        if (_loadSection == null) return false;
        _loadSection = null;
        return true;
    }

    /// <summary>Advance the load section PC (for labels inside LOAD).</summary>
    public void AdvanceLoad(int bytes)
    {
        if (_loadSection != null)
            _sectionPCs[_loadSection] += bytes;
    }
}
