namespace Koh.Core.Binding;

public sealed class SectionManager
{
    private readonly Dictionary<string, SectionBuffer> _sections = new(StringComparer.OrdinalIgnoreCase);
    private SectionBuffer? _activeSection;
    private readonly Stack<string?> _sectionStack = new();

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

    public IReadOnlyDictionary<string, SectionBuffer> AllSections => _sections;
}
