using Koh.Common;

namespace Koh.Objects;

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

    /// <summary>
    /// Deserialization constructor. Diagnostics are not stored in .kobj — pass an explicit
    /// <paramref name="success"/> flag derived from the original compilation result.
    /// The only valid caller is <see cref="KobjReader"/>; all other paths should
    /// use the diagnostics-based overload.
    /// </summary>
    public EmitModel(
        IReadOnlyList<SectionData> sections,
        IReadOnlyList<SymbolData> symbols,
        bool success
    )
    {
        Sections = sections;
        Symbols = symbols;
        Diagnostics = [];
        Success = success;
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

    /// <summary>
    /// Per-byte source location map, coalesced into ranges. Survives
    /// round-trip through .kobj v2; the linker converts each entry's
    /// section-relative offset into an absolute bank+address when it
    /// builds the .kdbg debug info.
    /// </summary>
    public IReadOnlyList<LineMapEntry> LineMap { get; }

    public SectionData(
        string name,
        SectionType type,
        int? fixedAddress,
        int? bank,
        byte[] data,
        IReadOnlyList<PatchEntry> patches,
        IReadOnlyList<LineMapEntry>? lineMap = null
    )
    {
        Name = name;
        Type = type;
        FixedAddress = fixedAddress;
        Bank = bank;
        Data = data;
        Patches = patches;
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
