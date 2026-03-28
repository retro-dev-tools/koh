using Koh.Core.Diagnostics;
using Koh.Core.Symbols;

namespace Koh.Linker.Core;

/// <summary>
/// Resolves symbols across multiple object files. Exported symbols are
/// visible globally; local symbols are scoped to their source file.
/// </summary>
public sealed class SymbolResolver
{
    private readonly Dictionary<string, LinkerSymbol> _globals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LinkerSymbol> _allSymbols = [];
    private readonly DiagnosticBag _diagnostics;

    public SymbolResolver(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    public IReadOnlyList<LinkerSymbol> AllSymbols => _allSymbols;

    /// <summary>
    /// Add all symbols from a linker input. Exported symbols are registered
    /// globally; duplicates produce diagnostics.
    /// </summary>
    public void AddSymbols(LinkerInput input)
    {
        foreach (var sym in input.Model.Symbols)
        {
            var linkerSym = new LinkerSymbol(sym, input.FilePath);
            _allSymbols.Add(linkerSym);

            if (sym.Visibility == SymbolVisibility.Exported)
            {
                if (_globals.TryGetValue(sym.Name, out var existing))
                {
                    _diagnostics.Report(default,
                        $"Duplicate exported symbol '{sym.Name}' " +
                        $"(defined in '{existing.SourceFile}' and '{input.FilePath}')");
                }
                else
                {
                    _globals[sym.Name] = linkerSym;
                }
            }
        }
    }

    /// <summary>
    /// Look up a symbol by name. Exported symbols are found globally.
    /// </summary>
    public LinkerSymbol? Lookup(string name)
    {
        _globals.TryGetValue(name, out var sym);
        return sym;
    }

    /// <summary>
    /// Update absolute addresses for all symbols after section placement.
    /// Label symbols get their section's placed address + their section-relative value.
    /// Constants keep their original value.
    /// </summary>
    public void ResolveAddresses(IReadOnlyList<LinkerSection> sections)
    {
        // TryAdd: first section with a given name wins. Duplicate section names across
        // object files are merged by RGBDS — Koh's linker currently uses first-wins
        // semantics. Duplicate names with conflicting types will produce wrong addresses.
        var sectionMap = new Dictionary<string, LinkerSection>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sections)
            sectionMap.TryAdd(s.Name, s);

        foreach (var sym in _allSymbols)
        {
            if (sym.Kind == SymbolKind.Constant)
            {
                sym.AbsoluteAddress = sym.Value;
                continue;
            }

            if (sym.SectionName != null && sectionMap.TryGetValue(sym.SectionName, out var section))
            {
                sym.AbsoluteAddress = section.PlacedAddress + sym.Value;
                sym.PlacedBank = section.PlacedBank;
            }
        }
    }
}
