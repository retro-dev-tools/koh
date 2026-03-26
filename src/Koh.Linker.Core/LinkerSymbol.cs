using Koh.Core.Binding;
using Koh.Core.Symbols;

namespace Koh.Linker.Core;

/// <summary>
/// A symbol in the linker's global namespace. Tracks which object file
/// defined it and its resolved absolute address after section placement.
/// </summary>
public sealed class LinkerSymbol
{
    public string Name { get; }
    public SymbolKind Kind { get; }
    public SymbolVisibility Visibility { get; }
    public long Value { get; internal set; }
    public string? SectionName { get; }
    public string SourceFile { get; }

    /// <summary>
    /// Absolute address after section placement. -1 if not yet placed.
    /// For ROMX symbols this is the windowed GB address (0x4000–0x7FFF), not the flat offset.
    /// </summary>
    public long AbsoluteAddress { get; internal set; } = -1;

    /// <summary>
    /// Bank number after section placement. 0 for ROM0/RAM/other fixed-window regions.
    /// Set by <see cref="SymbolResolver.ResolveAddresses"/>.
    /// </summary>
    public int PlacedBank { get; internal set; }

    public LinkerSymbol(SymbolData sym, string sourceFile)
    {
        Name = sym.Name;
        Kind = sym.Kind;
        Visibility = sym.Visibility;
        Value = sym.Value;
        SectionName = sym.Section;
        SourceFile = sourceFile;
    }
}
