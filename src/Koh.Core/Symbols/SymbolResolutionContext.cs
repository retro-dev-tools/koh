namespace Koh.Core.Symbols;

public readonly record struct SymbolResolutionContext(
    string OwnerId,
    string? CurrentFilePath = null);
