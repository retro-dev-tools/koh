namespace Koh.Core.Binding;

/// <summary>
/// Lightweight PC tracking for Pass 1 — no byte arrays, just counters.
/// </summary>
internal sealed class SectionPCTracker
{
    private readonly Dictionary<string, int> _sectionPCs = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeSection;

    public int CurrentPC => _activeSection != null && _sectionPCs.TryGetValue(_activeSection, out var pc)
        ? pc : 0;

    public string? ActiveSectionName => _activeSection;

    public void SetActive(string sectionName, int basePC)
    {
        _activeSection = sectionName;
        if (!_sectionPCs.ContainsKey(sectionName))
            _sectionPCs[sectionName] = basePC;
    }

    public void Advance(int bytes)
    {
        if (_activeSection != null)
            _sectionPCs[_activeSection] += bytes;
    }
}
