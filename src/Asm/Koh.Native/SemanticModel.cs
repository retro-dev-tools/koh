using Koh.Common;
using Koh.Native.Symbols;
using Koh.Native.Syntax;
using Koh.Objects;

namespace Koh.Native;

public sealed class SemanticModel
{
    private readonly Compilation compilation;
    private readonly SyntaxTree tree;
    private Symbol[]? symbols;

    internal SemanticModel(Compilation compilation, SyntaxTree tree)
    {
        this.compilation = compilation;
        this.tree = tree;
    }

    private Symbol[] Symbols =>
        symbols ??= compilation
            .Analysis.Symbols.Select(data => new Symbol(data, compilation.Documents))
            .ToArray();

    public Symbol? ResolveSymbol(string rawName, int position)
    {
        var path = SyntaxTree.SourcePath(tree.Text);
        var exact = Symbols.FirstOrDefault(symbol =>
            (
                symbol.Name == rawName
                || (
                    rawName.StartsWith('.')
                    && symbol.Name.EndsWith(rawName, StringComparison.Ordinal)
                )
            )
            && symbol
                .Data.References.Append(symbol.Data.Definition)
                .Any(location =>
                    location != null && location.Path == path && location.Start == position
                )
        );
        if (exact != null)
            return exact;
        var name = rawName;
        if (rawName.StartsWith('.'))
        {
            var scope = tree.Document.Scopes.LastOrDefault(scope =>
                scope.Start <= position && position < scope.End
            );
            if (scope == null)
                return null;
            name = scope.Name + rawName;
        }
        return Symbols.FirstOrDefault(symbol =>
                symbol.Name == name
                && (symbol.OwnerId == path || symbol.Visibility == SymbolVisibility.Exported)
            )
            ?? Symbols.FirstOrDefault(symbol =>
                symbol.Name == name && compilation.SyntaxTrees.Count == 1
            );
    }

    public IEnumerable<Symbol> LookupSymbols(int position) =>
        Symbols.Where(symbol =>
            symbol.Visibility == SymbolVisibility.Exported
            || symbol.OwnerId == SyntaxTree.SourcePath(tree.Text)
        );

    public int? GetMacroArity(Symbol symbol) =>
        symbol.Kind == SymbolKind.Macro ? symbol.Data.MacroArity : null;

    public int? GetMacroArity(string name) =>
        ResolveSymbol(name, 0) is { } symbol ? GetMacroArity(symbol) : null;

    public IReadOnlyList<Diagnostic> GetDiagnostics() =>
        compilation.Analysis.Diagnostics.Select(d => d.ToDiagnostic()).ToArray();
}
