using Koh.Native.Syntax;
using Koh.Objects;

namespace Koh.Native.Symbols;

public enum SymbolState
{
    Undefined,
    Defined,
}

public sealed class Symbol
{
    internal NativeSymbol Data { get; }
    private readonly IReadOnlyDictionary<string, SyntaxTree> documents;

    internal Symbol(NativeSymbol data, IReadOnlyDictionary<string, SyntaxTree> documents)
    {
        Data = data;
        this.documents = documents;
        Kind = Enum.Parse<SymbolKind>(data.Kind);
        Visibility = Enum.Parse<SymbolVisibility>(data.Visibility);
    }

    public string Name => Data.Name;
    public string SymbolId => Data.Id;
    public string? OwnerId => Data.Owner;
    public SymbolKind Kind { get; }
    public SymbolVisibility Visibility { get; }
    public long? Value => Data.Value;
    public SymbolState State =>
        Kind == SymbolKind.Constant && Data.Value == null
            ? SymbolState.Undefined
            : SymbolState.Defined;
    public string? Section => Data.Section;
    public SyntaxNode? DefinitionSite =>
        Data.Definition is { } location && documents.TryGetValue(location.Path, out var tree)
            ? tree.Root.FindToken(location.Start)?.Parent
            : null;
    public string? DefinitionFilePath => Data.Definition?.Path;
}
