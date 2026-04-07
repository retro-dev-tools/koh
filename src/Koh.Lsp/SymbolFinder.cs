using Koh.Core.Symbols;
using Koh.Core.Syntax;
using CoreSymbolKind = Koh.Core.Symbols.SymbolKind;

namespace Koh.Lsp;

/// <summary>
/// Single authority for resolving rename/reference targets, finding all semantic
/// occurrences, context gating, collision checks, and name validation.
/// All operations use compiler semantic models and SymbolId — never raw text matching.
/// </summary>
internal sealed class SymbolFinder
{
    internal sealed record ResolvedSymbol(
        Symbol Symbol,
        SyntaxToken Token,
        string Uri,
        string OwnerId,
        bool IsDeclaration);

    /// <summary>
    /// Resolve the symbol at a given offset in a document.
    /// Returns null if the token is not in a symbol-bearing context or semantic resolution fails.
    /// </summary>
    public ResolvedSymbol? ResolveAt(Workspace workspace, string uri, int offset)
    {
        var doc = workspace.GetDocument(uri);
        if (doc == null) return null;

        var (source, tree) = doc.Value;
        var token = tree.Root.FindToken(offset);
        if (token == null) return null;

        // Reject non-identifier tokens
        if (token.Kind is not SyntaxKind.IdentifierToken and not SyntaxKind.LocalLabelToken)
            return null;

        // Require symbol-bearing ancestor context
        var parent = token.Parent;
        if (parent == null) return null;
        if (!IsSymbolContext(parent.Kind))
            return null;

        // Resolve via compilation semantic model
        var model = workspace.GetSemanticModel(uri);
        if (model == null) return null;

        var symbol = model.ResolveSymbol(token.Text, token.Span.Start);
        if (symbol == null) return null;

        bool isDeclaration = IsDeclarationContext(parent.Kind);
        var ownerId = symbol.OwnerId ?? uri;

        return new ResolvedSymbol(symbol, token, uri, ownerId, isDeclaration);
    }

    /// <summary>
    /// Find all occurrences of a resolved symbol across loaded documents.
    /// Uses SymbolId matching — never raw text matching.
    /// </summary>
    public IReadOnlyList<ResolvedSymbol> FindAllOccurrences(
        Workspace workspace,
        ResolvedSymbol target,
        bool includeDeclarations = true)
    {
        var results = new List<ResolvedSymbol>();
        var targetId = target.Symbol.SymbolId;
        var seen = new HashSet<(string Uri, int Start)>();

        foreach (var uri in workspace.OpenDocumentUris)
        {
            var doc = workspace.GetDocument(uri);
            if (doc == null) continue;

            var (source, tree) = doc.Value;
            var model = workspace.GetSemanticModel(uri);
            if (model == null) continue;

            WalkForOccurrences(tree.Root, model, uri, targetId, includeDeclarations, results, seen);
        }

        return results;
    }

    /// <summary>
    /// Validate whether a rename is allowed. Returns null if valid, or an error message if not.
    /// </summary>
    public string? ValidateRename(
        Workspace workspace,
        ResolvedSymbol target,
        string newName)
    {
        // Lexical identifier validity
        if (string.IsNullOrWhiteSpace(newName))
            return "New name cannot be empty.";

        // Local/global form preservation
        bool isLocal = target.Symbol.Name.StartsWith('.') || target.Symbol.Name.Contains('.');
        bool newIsLocal = newName.StartsWith('.');

        if (isLocal && !newIsLocal)
            return "Cannot rename a local label to a global form.";
        if (!isLocal && newIsLocal)
            return "Cannot rename a global symbol to a local form.";

        // Basic identifier character validation
        if (!IsValidIdentifier(newName))
            return $"'{newName}' is not a valid identifier.";

        // Keyword rejection
        if (Lexer.IsKeyword(newName))
            return $"'{newName}' is a reserved keyword.";

        // Register-name rejection
        if (IsRegisterName(newName))
            return $"'{newName}' is a register name.";

        // Collision checks
        var collisionError = CheckCollisions(workspace, target, newName);
        if (collisionError != null)
            return collisionError;

        return null;
    }

    // =========================================================================
    // Context gating
    // =========================================================================

    /// <summary>
    /// Returns true if the node kind is a valid context for symbol declaration or reference.
    /// </summary>
    private static bool IsSymbolContext(SyntaxKind kind) =>
        IsDeclarationContext(kind) || IsReferenceContext(kind);

    private static bool IsDeclarationContext(SyntaxKind kind) =>
        kind is SyntaxKind.LabelDeclaration or SyntaxKind.SymbolDirective or SyntaxKind.MacroDefinition;

    private static bool IsReferenceContext(SyntaxKind kind) =>
        kind is SyntaxKind.NameExpression or SyntaxKind.LabelOperand or SyntaxKind.MacroCall;

    // =========================================================================
    // Occurrence walking
    // =========================================================================

    private void WalkForOccurrences(
        SyntaxNode node,
        Core.SemanticModel model,
        string uri,
        (string? OwnerId, string QualifiedName) targetId,
        bool includeDeclarations,
        List<ResolvedSymbol> results,
        HashSet<(string Uri, int Start)> seen)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
            {
                var childNode = child.AsNode!;

                // Check if this node is an EXPORT directive that references our symbol
                // EXPORT directive structure: [ExportKeyword, IdentifierToken, CommaToken, IdentifierToken, ...]
                if (childNode.Kind == SyntaxKind.SymbolDirective)
                {
                    var tokens = childNode.ChildTokens().ToList();
                    if (tokens.Count >= 2 && tokens[0].Kind == SyntaxKind.ExportKeyword)
                    {
                        for (int i = 1; i < tokens.Count; i++)
                        {
                            var nameToken = tokens[i];
                            if (nameToken.Kind != SyntaxKind.IdentifierToken) continue;

                            var resolved = model.ResolveSymbol(nameToken.Text, nameToken.Span.Start);
                            if (resolved != null && resolved.SymbolId == targetId)
                            {
                                var exportKey = (uri, nameToken.Span.Start);
                                if (seen.Add(exportKey))
                                {
                                    var ownerId = resolved.OwnerId ?? uri;
                                    results.Add(new ResolvedSymbol(resolved, nameToken, uri, ownerId, IsDeclaration: false));
                                }
                            }
                        }
                        // Don't recurse further into this directive — we handled it
                        continue;
                    }
                }

                WalkForOccurrences(childNode, model, uri, targetId, includeDeclarations, results, seen);
                continue;
            }

            if (!child.IsToken) continue;
            var token = child.AsToken!;

            if (token.Kind is not SyntaxKind.IdentifierToken and not SyntaxKind.LocalLabelToken)
                continue;

            var parent = token.Parent;
            if (parent == null || !IsSymbolContext(parent.Kind))
                continue;

            bool isDecl = IsDeclarationContext(parent.Kind);
            if (isDecl && !includeDeclarations)
                continue;

            var sym = model.ResolveSymbol(token.Text, token.Span.Start);
            if (sym == null) continue;
            if (sym.SymbolId != targetId) continue;

            var dedupeKey = (uri, token.Span.Start);
            if (!seen.Add(dedupeKey)) continue;

            var ownerIdForResult = sym.OwnerId ?? uri;
            results.Add(new ResolvedSymbol(sym, token, uri, ownerIdForResult, isDecl));
        }
    }

    // =========================================================================
    // Validation helpers
    // =========================================================================

    private static bool IsValidIdentifier(string name)
    {
        var text = name.StartsWith('.') ? name[1..] : name;
        if (text.Length == 0) return false;

        // First char must be letter or underscore
        if (!char.IsLetter(text[0]) && text[0] != '_')
            return false;

        for (int i = 1; i < text.Length; i++)
        {
            if (!char.IsLetterOrDigit(text[i]) && text[i] != '_' && text[i] != '#')
                return false;
        }

        return true;
    }

    private static bool IsRegisterName(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower is "a" or "b" or "c" or "d" or "e" or "f"
            or "h" or "l" or "af" or "bc" or "de" or "hl" or "sp";
    }

    private string? CheckCollisions(Workspace workspace, ResolvedSymbol target, string newName)
    {
        var targetSymbol = target.Symbol;

        // For each document, check if the new name would collide with an existing symbol
        foreach (var uri in workspace.OpenDocumentUris)
        {
            var model = workspace.GetSemanticModel(uri);
            if (model == null) continue;

            // Build the qualified name for local labels
            string qualifiedNewName;
            if (newName.StartsWith('.') && targetSymbol.Name.Contains('.'))
            {
                // Local label: preserve the global anchor prefix
                var dotIndex = targetSymbol.Name.IndexOf('.');
                qualifiedNewName = targetSymbol.Name[..dotIndex] + newName;
            }
            else
            {
                qualifiedNewName = newName;
            }

            // Check if symbol with new name already exists
            var existing = model.ResolveSymbol(qualifiedNewName, 0);
            if (existing != null && existing.SymbolId != targetSymbol.SymbolId)
            {
                // Check visibility rules
                if (targetSymbol.Visibility == SymbolVisibility.Exported)
                {
                    // Exported collision — check if existing is also exported
                    if (existing.Visibility == SymbolVisibility.Exported)
                        return $"An exported symbol named '{newName}' already exists.";
                }

                // Owner-local collision
                if (existing.OwnerId == targetSymbol.OwnerId ||
                    existing.Visibility == SymbolVisibility.Exported ||
                    targetSymbol.Visibility == SymbolVisibility.Exported)
                {
                    return $"A symbol named '{newName}' already exists in the same scope.";
                }
            }
        }

        return null;
    }
}
