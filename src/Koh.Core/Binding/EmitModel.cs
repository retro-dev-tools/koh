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
    /// Deserialization constructor. Diagnostics are not stored in .kobj — pass an explicit
    /// <paramref name="success"/> flag derived from the original compilation result.
    /// The only valid caller is <see cref="Koh.Emit.KobjReader"/>; all other paths should
    /// use the diagnostics-based overload.
    /// </summary>
    internal EmitModel(IReadOnlyList<SectionData> sections,
        IReadOnlyList<SymbolData> symbols,
        bool success)
    {
        Sections = sections;
        Symbols = symbols;
        Diagnostics = [];
        Success = success;
    }

    /// <summary>
    /// Build a frozen EmitModel from the live binding state.
    /// </summary>
    internal static EmitModel FromBindingResult(BindingResult result)
    {
        var sections = new List<SectionData>();
        if (result.Sections != null)
        {
            // Sort sections by alignment bits descending (tighter alignment first), then by
            // insertion order for sections with the same alignment. This matches the linker's
            // placement strategy and produces deterministic output for multi-section assemblies.
            var orderedSections = result.Sections
                .OrderByDescending(kv => kv.Value.AlignBits)
                .ThenByDescending(kv => kv.Value.FixedAddress.HasValue ? 1 : 0);
            foreach (var (name, buf) in orderedSections)
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
                if (sym.State == SymbolState.Defined)
                {
                    symbols.Add(new SymbolData(
                        sym.Name, sym.Kind, sym.Visibility,
                        sym.Section, sym.Value));
                }
                else if (sym.State == SymbolState.Undefined && sym.DefinitionSite == null)
                {
                    // Truly undefined (no definition in this file) — mark as import
                    symbols.Add(new SymbolData(
                        sym.Name, sym.Kind, SymbolVisibility.Imported,
                        null, 0));
                }
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
