namespace Koh.Core.Binding;

public sealed class SectionManager
{
    private readonly Dictionary<string, SectionBuffer> _sections = new(StringComparer.OrdinalIgnoreCase);
    private SectionBuffer? _activeSection;
    private readonly Stack<string?> _sectionStack = new();

    // Union state — stack for nested UNION support
    private readonly record struct UnionState(int StartOffset, int MaxOffset);
    private readonly Stack<UnionState> _unionStack = new();

    // LOAD state: data goes to enclosing section, labels go to load section
    private SectionBuffer? _loadSection;
    private SectionBuffer? _enclosingSection;

    public SectionBuffer? ActiveSection => _activeSection;

    public SectionBuffer OpenOrResume(string name, SectionType type,
        int? fixedAddress = null, int? bank = null)
    {
        if (_sections.TryGetValue(name, out var existing))
        {
            _activeSection = existing;
            return existing;
        }

        var section = new SectionBuffer(name, type, fixedAddress, bank);
        _sections[name] = section;
        _activeSection = section;
        return section;
    }

    /// <summary>
    /// Push the current section name onto the stack (PUSHS).
    /// </summary>
    public void PushSection() => _sectionStack.Push(_activeSection?.Name);

    /// <summary>
    /// Pop and restore the previous section (POPS).
    /// Returns false if the stack is empty.
    /// </summary>
    public bool PopSection()
    {
        if (_sectionStack.Count == 0) return false;
        var name = _sectionStack.Pop();
        _activeSection = name != null && _sections.TryGetValue(name, out var s) ? s : null;
        return true;
    }

    // --- Union support ---

    public void BeginUnion()
    {
        if (_activeSection == null) return;
        _unionStack.Push(new UnionState(_activeSection.CurrentOffset, _activeSection.CurrentOffset));
    }

    public bool NextUnion()
    {
        if (_unionStack.Count == 0 || _activeSection == null) return false;
        var state = _unionStack.Pop();
        int maxOffset = Math.Max(state.MaxOffset, _activeSection.CurrentOffset);
        // Truncate bytes back to union start for next member
        _activeSection.TruncateTo(state.StartOffset);
        _unionStack.Push(new UnionState(state.StartOffset, maxOffset));
        return true;
    }

    public bool EndUnion()
    {
        if (_unionStack.Count == 0 || _activeSection == null) return false;
        var state = _unionStack.Pop();
        int maxOffset = Math.Max(state.MaxOffset, _activeSection.CurrentOffset);
        // Pad to max member size
        while (_activeSection.CurrentOffset < maxOffset)
            _activeSection.EmitByte(0x00);
        return true;
    }

    // --- LOAD support ---

    public bool IsInLoad => _loadSection != null;

    public void BeginLoad(string name, SectionType type, int? fixedAddress, int? bank)
    {
        if (_loadSection != null)
        {
            // Implicitly end the current LOAD before starting a new one
            EndLoad();
        }
        _enclosingSection = _activeSection;
        var loadBuf = OpenOrResume(name, type, fixedAddress, bank);
        _loadSection = loadBuf;
        // Active section stays as the enclosing section for data emission
        _activeSection = _enclosingSection;
    }

    public bool EndLoad()
    {
        if (_loadSection == null) return false;
        _loadSection = null;
        _enclosingSection = null;
        return true;
    }

    public IReadOnlyDictionary<string, SectionBuffer> AllSections => _sections;
}
