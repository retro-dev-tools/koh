using Koh.Core.Symbols;

namespace Koh.Linker.Core;

/// <summary>
/// Writes a .sym file (debug symbols) compatible with BGB and other emulators.
/// Format: one line per symbol, "BB:AAAA SymbolName"
/// </summary>
public static class SymFileWriter
{
    public static void Write(TextWriter writer, IReadOnlyList<LinkerSymbol> symbols)
    {
        // Header comment
        writer.WriteLine("; koh-link symbol file");

        foreach (var sym in symbols.OrderBy(s => s.AbsoluteAddress))
        {
            if (sym.Kind == SymbolKind.Constant) continue; // skip EQU constants
            if (sym.AbsoluteAddress < 0) continue; // not placed

            // AbsoluteAddress is already the windowed GB address (0x0000–0x7FFF).
            // PlacedBank carries the correct bank number set by SymbolResolver.
            int bank = sym.SectionName != null ? sym.PlacedBank : 0;
            int addr = (int)(sym.AbsoluteAddress & 0xFFFF);
            writer.WriteLine($"{bank:X2}:{addr:X4} {sym.Name}");
        }
    }
}
