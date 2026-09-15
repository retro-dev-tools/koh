using Koh.Assembler.Symbols;
using Koh.Objects;

namespace Koh.Assembler.Binding;

internal static class EmitModelBuilder
{
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
            var orderedSections = result
                .Sections.OrderByDescending(kv => kv.Value.AlignBits)
                .ThenByDescending(kv => kv.Value.FixedAddress.HasValue ? 1 : 0);
            foreach (var (name, buf) in orderedSections)
            {
                sections.Add(
                    new SectionData(
                        name,
                        buf.Type,
                        buf.FixedAddress,
                        buf.Bank,
                        buf.Bytes.ToArray(),
                        buf.Patches.ToList(),
                        buf.LineMap.ToList()
                    )
                );
            }
        }

        var symbols = new List<SymbolData>();
        if (result.Symbols != null)
        {
            foreach (var sym in result.Symbols.AllSymbols)
            {
                if (sym.State == SymbolState.Defined)
                {
                    symbols.Add(
                        new SymbolData(sym.Name, sym.Kind, sym.Visibility, sym.Section, sym.Value)
                    );
                }
                else if (sym.State == SymbolState.Undefined && sym.DefinitionSite == null)
                {
                    // Truly undefined (no definition in this file) — mark as import
                    symbols.Add(
                        new SymbolData(sym.Name, sym.Kind, SymbolVisibility.Imported, null, 0)
                    );
                }
            }
        }

        return new EmitModel(sections, symbols, result.Diagnostics);
    }
}
