using Koh.Core.Symbols;

namespace Koh.Linker.Core;

public static class DebugInfoPopulator
{
    /// <summary>
    /// Populate a <see cref="DebugInfoBuilder"/> from linker symbols and their
    /// source locations. Phase 1 only emits the symbol table and a best-effort
    /// address map (one entry per symbol, byte count = 1). Full per-byte
    /// address mapping with expansion stacks arrives in Phase 3 alongside the
    /// coalescing and dedup optimizations.
    /// </summary>
    public static void PopulateFromLinkerSymbols(
        DebugInfoBuilder builder,
        IReadOnlyList<LinkerSymbol> symbols)
    {
        foreach (var sym in symbols)
        {
            if (sym.AbsoluteAddress < 0) continue;   // unplaced
            byte bank = (byte)(sym.SectionName != null ? sym.PlacedBank : 0);
            ushort address = (ushort)(sym.AbsoluteAddress & 0xFFFF);
            var kind = sym.Kind switch
            {
                SymbolKind.Constant => KdbgSymbolKind.EquConstant,
                SymbolKind.Label    => KdbgSymbolKind.Label,
                _                   => KdbgSymbolKind.Label,
            };

            builder.AddSymbol(
                kind: kind,
                bank: bank,
                address: address,
                size: 0,
                name: sym.Name,
                scopeId: 0,
                definitionSourceFile: sym.SourceFile,
                definitionLine: 0);

            if (kind != KdbgSymbolKind.EquConstant)
            {
                builder.AddAddressMapping(
                    bank: bank,
                    address: address,
                    byteCount: 1,
                    sourceFile: sym.SourceFile ?? "",
                    line: 0);
            }
        }
    }
}
