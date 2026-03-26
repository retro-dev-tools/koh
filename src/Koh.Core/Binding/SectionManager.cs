namespace Koh.Core.Binding;

public sealed class SectionManager
{
    private readonly Dictionary<string, SectionBuffer> _sections = new(StringComparer.OrdinalIgnoreCase);
    private SectionBuffer? _activeSection;

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

    public IReadOnlyDictionary<string, SectionBuffer> AllSections => _sections;
}
