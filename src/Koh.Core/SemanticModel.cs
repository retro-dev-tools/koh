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
    private readonly string _ownerId;

    internal SemanticModel(SyntaxTree tree, BindingResult result)
    {
        _tree = tree;
        _result = result;
        _ownerId = string.IsNullOrEmpty(tree.Text.FilePath) ? "<anonymous>" : tree.Text.FilePath;
    }

    /// <summary>
    /// Authoritative post-binding resolution API. Resolves a symbol by raw name
    /// at a specific source position. For local labels (.xxx), walks top-level
    /// children to find the nearest preceding global LabelDeclaration, then
    /// looks up the qualified name via LookupQualified.
    /// </summary>
    public Symbol? ResolveSymbol(string rawName, int position)
    {
        if (_result.Symbols == null) return null;

        var context = new SymbolResolutionContext(_ownerId, _tree.Text.FilePath);

        if (rawName.StartsWith('.'))
        {
            string? scope = null;
            foreach (var node in _tree.Root.ChildNodes())
            {
                if (node.Position > position) break;
                if (node.Kind == SyntaxKind.LabelDeclaration)
                {
                    var token = node.ChildTokens().FirstOrDefault();
                    if (token != null && token.Kind == SyntaxKind.IdentifierToken)
                        scope = token.Text;
                }
            }
            if (scope == null) return null;

            return _result.Symbols.LookupQualified(scope + rawName, context);
        }

        return _result.Symbols.LookupQualified(rawName, context);
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
            {
                var name = nameToken.Text;
                return ResolveSymbol(name, node.Position);
            }
        }

        if (node.Kind == SyntaxKind.SymbolDirective)
        {
            var tokens = node.ChildTokens().ToList();
            // identifier EQU/EQUS ... — first token is the name
            if (tokens.Count >= 2 && tokens[0].Kind == SyntaxKind.IdentifierToken)
                return ResolveSymbol(tokens[0].Text, node.Position);
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
                return ResolveSymbol(token.Text, node.Position);
        }

        return null;
    }

    /// <summary>
    /// Get all defined symbols visible at the given position.
    /// Global symbols are always visible. Local labels (starting with '.') are filtered
    /// to the enclosing global label scope at the given position.
    /// Only shows owner-local symbols for this owner plus all exported symbols.
    /// </summary>
    public IEnumerable<Symbol> LookupSymbols(int position)
    {
        if (_result.Symbols == null)
            return Enumerable.Empty<Symbol>();

        // Find the enclosing global label for scope filtering
        string? currentScope = null;
        foreach (var node in _tree.Root.ChildNodes())
        {
            if (node.Position > position) break;
            if (node.Kind == SyntaxKind.LabelDeclaration)
            {
                var token = node.ChildTokens().FirstOrDefault();
                if (token != null && token.Kind == SyntaxKind.IdentifierToken)
                    currentScope = token.Text;
            }
        }

        return _result.Symbols.GetVisibleSymbols(_ownerId)
            .Where(s => s.State == SymbolState.Defined)
            .Where(s =>
            {
                if (!s.Name.Contains('.')) return true; // global symbol — always visible
                // Local labels stored as "globalName.localName" — visible in matching scope
                if (currentScope == null) return false; // no enclosing scope — local not visible
                return s.Name.StartsWith(currentScope + ".", StringComparison.OrdinalIgnoreCase);
            });
    }

    /// <summary>
    /// Get the maximum observed call-site argument count for a macro symbol.
    /// Returns null if the macro was never called or the symbol is not a macro.
    /// </summary>
    public int? GetMacroArity(Symbol symbol)
    {
        if (symbol.Kind != SymbolKind.Macro) return null;
        if (_result.MacroArities == null) return null;
        return _result.MacroArities.TryGetValue(symbol, out var arity) ? arity : null;
    }

    /// <summary>
    /// Convenience overload: resolves the macro symbol by name, then queries arity.
    /// Returns null if the macro is not found or was never called.
    /// </summary>
    public int? GetMacroArity(string macroName)
    {
        var symbol = ResolveSymbol(macroName, 0);
        if (symbol == null) return null;
        return GetMacroArity(symbol);
    }

    /// <summary>
    /// Get diagnostics from the binding phase.
    /// Returns all diagnostics from the compilation. For per-file filtering in multi-file
    /// scenarios, use the Workspace.GetDocumentDiagnostics approach (SyntaxTree diagnostics
    /// for parse errors, single-file binding diagnostics when only one file is open).
    /// </summary>
    public IReadOnlyList<Diagnostic> GetDiagnostics() => _result.Diagnostics;
}
