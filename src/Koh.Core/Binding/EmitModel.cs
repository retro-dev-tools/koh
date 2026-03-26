using Koh.Core.Diagnostics;
using Koh.Core.Symbols;

namespace Koh.Core.Binding;

/// <summary>
/// Frozen output of the binding phase. Contains assembled sections, resolved symbols,
/// and diagnostics. Consumed by the linker and .kobj writer.
/// </summary>
public sealed class EmitModel
{
    public IReadOnlyList<SectionData> Sections { get; }
    public IReadOnlyList<SymbolData> Symbols { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool Success { get; }

    internal EmitModel(IReadOnlyList<SectionData> sections,
        IReadOnlyList<SymbolData> symbols,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        Sections = sections;
        Symbols = symbols;
        Diagnostics = diagnostics;

        Success = true;
        for (int i = 0; i < diagnostics.Count; i++)
            if (diagnostics[i].Severity == DiagnosticSeverity.Error)
            {
                Success = false;
                break;
            }
    }

    /// <summary>
    /// Build a frozen EmitModel from the live binding state.
    /// </summary>
    internal static EmitModel FromBindingResult(BindingResult result)
    {
        var sections = new List<SectionData>();
        if (result.Sections != null)
        {
            foreach (var (name, buf) in result.Sections)
            {
                sections.Add(new SectionData(
                    name, buf.Type, buf.FixedAddress, buf.Bank,
                    buf.Bytes.ToArray(),
                    buf.Patches.ToList()));
            }
        }

        var symbols = new List<SymbolData>();
        if (result.Symbols != null)
        {
            foreach (var sym in result.Symbols.AllSymbols)
            {
                if (sym.State != SymbolState.Defined) continue;
                symbols.Add(new SymbolData(
                    sym.Name, sym.Kind, sym.Visibility,
                    sym.Section, sym.Value));
            }
        }

        return new EmitModel(sections, symbols, result.Diagnostics);
    }
}

/// <summary>
/// Frozen snapshot of a section's assembled data.
/// </summary>
public sealed class SectionData
{
    public string Name { get; }
    public SectionType Type { get; }
    public int? FixedAddress { get; }
    public int? Bank { get; }
    public byte[] Data { get; }
    public IReadOnlyList<PatchEntry> Patches { get; }

    internal SectionData(string name, SectionType type, int? fixedAddress, int? bank,
        byte[] data, IReadOnlyList<PatchEntry> patches)
    {
        Name = name;
        Type = type;
        FixedAddress = fixedAddress;
        Bank = bank;
        Data = data;
        Patches = patches;
    }
}

/// <summary>
/// Frozen snapshot of a resolved symbol.
/// </summary>
public sealed class SymbolData
{
    public string Name { get; }
    public SymbolKind Kind { get; }
    public SymbolVisibility Visibility { get; }
    public string? Section { get; }
    public long Value { get; }

    internal SymbolData(string name, SymbolKind kind, SymbolVisibility visibility,
        string? section, long value)
    {
        Name = name;
        Kind = kind;
        Visibility = visibility;
        Section = section;
        Value = value;
    }
}
