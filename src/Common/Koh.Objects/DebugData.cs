namespace Koh.Objects;

public enum DebugSymbolKind : byte
{
    Label,
    Constant,
    RamLabel,
}

public sealed record DebugData(
    IReadOnlyList<DebugSymbol> Symbols,
    IReadOnlyList<DebugAddressMapEntry> AddressMap
);

public sealed record DebugExpansionFrame(string File, uint Line, uint Column);

public sealed record DebugSymbol(
    DebugSymbolKind Kind,
    uint Bank,
    int Value,
    string Name,
    string? DefinitionFile,
    uint DefinitionLine,
    uint DefinitionColumn,
    IReadOnlyList<DebugExpansionFrame> ExpansionStack
)
{
    public ushort Address => checked((ushort)Value);
}

public sealed record DebugAddressMapEntry(
    uint Bank,
    uint ByteCount,
    ushort Address,
    string SourceFile,
    uint Line,
    IReadOnlyList<DebugExpansionFrame> ExpansionStack
);
