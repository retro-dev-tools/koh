using Koh.Common;

namespace Koh.Objects;

public sealed class EmitModel
{
    public byte[]? ElfImage { get; init; }
    public IReadOnlyList<SectionData> Sections { get; }
    public IReadOnlyList<SymbolData> Symbols { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool Success { get; }

    public EmitModel(
        IReadOnlyList<SectionData> sections,
        IReadOnlyList<SymbolData> symbols,
        IReadOnlyList<Diagnostic> diagnostics
    )
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

    public IReadOnlyList<LineMapEntry> LineMap { get; }

    public SectionData(
        string name,
        SectionType type,
        int? fixedAddress,
        int? bank,
        byte[] data,
        IReadOnlyList<LineMapEntry>? lineMap = null
    )
    {
        Name = name;
        Type = type;
        FixedAddress = fixedAddress;
        Bank = bank;
        Data = data;
        LineMap = lineMap ?? Array.Empty<LineMapEntry>();
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

    public SymbolData(
        string name,
        SymbolKind kind,
        SymbolVisibility visibility,
        string? section,
        long value
    )
    {
        Name = name;
        Kind = kind;
        Visibility = visibility;
        Section = section;
        Value = value;
    }
}
