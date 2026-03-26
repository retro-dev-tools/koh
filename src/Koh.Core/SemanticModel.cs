using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;

namespace Koh.Core;

/// <summary>
/// Per-file view into a compilation's binding results. Provides symbol resolution,
/// diagnostics, and semantic information for the LSP and analysis tools.
/// </summary>
public sealed class SemanticModel
{
    private readonly SyntaxTree _tree;
    private readonly BindingResult _result;

    internal SemanticModel(SyntaxTree tree, BindingResult result)
    {
        _tree = tree;
        _result = result;
    }

    /// <summary>
    /// Get the symbol declared by a label or EQU node.
    /// </summary>
    public Symbol? GetDeclaredSymbol(SyntaxNode node)
    {
        if (_result.Symbols == null) return null;

        if (node.Kind == SyntaxKind.LabelDeclaration)
        {
            var nameToken = node.ChildTokens().FirstOrDefault();
            if (nameToken != null)
                return _result.Symbols.Lookup(nameToken.Text);
        }

        if (node.Kind == SyntaxKind.SymbolDirective)
        {
            var tokens = node.ChildTokens().ToList();
            // identifier EQU ... — first token is the name
            if (tokens.Count >= 2 && tokens[0].Kind == SyntaxKind.IdentifierToken)
                return _result.Symbols.Lookup(tokens[0].Text);
        }

        return null;
    }

    /// <summary>
    /// Get the symbol referenced by a name expression or label operand.
    /// </summary>
    public Symbol? GetSymbol(SyntaxNode node)
    {
        if (_result.Symbols == null) return null;

        // NameExpression or LabelOperand — child token is the identifier
        if (node.Kind is SyntaxKind.NameExpression or SyntaxKind.LabelOperand)
        {
            var token = node.ChildTokens().FirstOrDefault();
            if (token != null)
                return _result.Symbols.Lookup(token.Text);
        }

        return null;
    }

    /// <summary>
    /// Get all defined symbols in the compilation.
    /// NOTE: The <paramref name="position"/> parameter is currently unused — all defined
    /// symbols are returned regardless of source position. Position-sensitive scoping
    /// requires a scope chain that does not exist yet (deferred to a later phase).
    /// </summary>
    public IEnumerable<Symbol> LookupSymbols(int position)
    {
        if (_result.Symbols == null)
            return Enumerable.Empty<Symbol>();

        return _result.Symbols.AllSymbols
            .Where(s => s.State == SymbolState.Defined);
    }

    /// <summary>
    /// Get diagnostics from the binding phase.
    /// NOTE: Returns diagnostics for the entire compilation, not just this file, because
    /// the binder accumulates all diagnostics in a single bag shared across trees.
    /// Per-file filtering requires <see cref="Diagnostic"/> to carry a source-tree
    /// reference (deferred to a later phase).
    /// </summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics() => _result.Diagnostics;
}
