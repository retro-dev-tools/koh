using Koh.Core.Syntax;

namespace Koh.Core.Symbols;

public enum SymbolKind
{
    Label,
    Constant,
    StringConstant,
    Macro,
}

public enum SymbolVisibility
{
    Local,
    Exported,
    Imported,
}

public enum SymbolState
{
    Undefined,
    Defined,
    Resolving,
}

public sealed class Symbol
{
    public string Name { get; }
    public SymbolKind Kind { get; }
    public SymbolState State { get; internal set; }
    public SymbolVisibility Visibility { get; internal set; }
    public long Value { get; internal set; }
    public string? Section { get; internal set; }
    public SyntaxNode? DefinitionSite { get; internal set; }
    public string? OwnerId { get; internal set; }

    public (string? OwnerId, string QualifiedName) SymbolId => (
        Visibility == SymbolVisibility.Exported ? null : OwnerId,
        Name);

    private readonly List<SyntaxNode> _referenceSites = [];
    public IReadOnlyList<SyntaxNode> ReferenceSites => _referenceSites;

    internal Symbol(string name, SymbolKind kind)
    {
        Name = name;
        Kind = kind;
        State = SymbolState.Undefined;
    }

    internal void Define(long value, SyntaxNode? site = null)
    {
        Value = value;
        State = SymbolState.Defined;
        DefinitionSite ??= site;
    }

    internal void AddReference(SyntaxNode site) => _referenceSites.Add(site);

    public bool HasReferences => _referenceSites.Count > 0;
}
