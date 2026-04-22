using Koh.Core.Symbols;

namespace Koh.Linker.Core;

public static class DebugInfoPopulator
{
    /// <summary>
    /// Populate symbol + address-map tables in <paramref name="builder"/>
    /// from a link result. Symbols are emitted for every placed label /
    /// constant; address-map entries come from the resolved per-byte
    /// line map so VS Code's <c>setBreakpoints</c> can turn a source
    /// file+line back into concrete (bank, address) breakpoint targets.
    ///
    /// kdbg AddressMapRecord.ByteCount is a single byte, so runs longer
    /// than 255 bytes are split into successive entries. Typical
    /// instructions are 1–3 bytes so this is only a concern for large
    /// data blocks (long DBs, DS fills, INCBIN).
    /// </summary>
    public static void Populate(DebugInfoBuilder builder, LinkResult result)
    {
        foreach (var sym in result.Symbols)
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

            // sym.SourceFile is the .kobj input path (not the .asm path)
            // and we don't thread the definition line yet — real source
            // attribution happens via the address map below. The symbol
            // table still carries names so "go to symbol" works.
            builder.AddSymbol(
                kind: kind,
                bank: bank,
                address: address,
                size: 0,
                name: sym.Name,
                scopeId: 0,
                definitionSourceFile: null,
                definitionLine: 0);
        }

        foreach (var run in result.LineMap)
        {
            int remaining = run.ByteCount;
            ushort cursor = run.Address;
            while (remaining > 0)
            {
                int chunk = remaining > 255 ? 255 : remaining;
                builder.AddAddressMapping(
                    bank: run.Bank,
                    address: cursor,
                    byteCount: (byte)chunk,
                    sourceFile: run.File,
                    line: run.Line);
                cursor = (ushort)((cursor + chunk) & 0xFFFF);
                remaining -= chunk;
            }
        }
    }

    /// <summary>
    /// Back-compat shim for callers that still pass just a symbol list.
    /// Equivalent to <see cref="Populate"/> with an empty line map —
    /// used by legacy tests that predate the linker exposing
    /// <see cref="LinkResult.LineMap"/>.
    /// </summary>
    public static void PopulateFromLinkerSymbols(
        DebugInfoBuilder builder,
        IReadOnlyList<LinkerSymbol> symbols)
    {
        foreach (var sym in symbols)
        {
            if (sym.AbsoluteAddress < 0) continue;
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
                definitionSourceFile: null,
                definitionLine: 0);
        }
    }
}
