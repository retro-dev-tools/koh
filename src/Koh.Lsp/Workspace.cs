using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Lsp;

/// <summary>
/// Holds open documents and maintains a live Compilation.
/// Thread-safe: all mutations go through a lock.
/// </summary>
internal sealed class Workspace
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (SourceText Text, SyntaxTree Tree)> _documents = new(StringComparer.OrdinalIgnoreCase);
    private Compilation? _compilation;
    private volatile EmitModel? _cachedModel;

    public void OpenDocument(string path, string text)
    {
        lock (_lock)
        {
            var source = SourceText.From(text, path);
            var tree = SyntaxTree.Parse(source);
            _documents[path] = (source, tree);
            RebuildCompilation();
        }
    }

    public void ChangeDocument(string path, string text)
    {
        lock (_lock)
        {
            var source = SourceText.From(text, path);
            var tree = SyntaxTree.Parse(source);
            _documents[path] = (source, tree);
            RebuildCompilation();
        }
    }

    public void CloseDocument(string uri)
    {
        lock (_lock)
        {
            _documents.Remove(uri);
            RebuildCompilation();
        }
    }

    /// <summary>
    /// Get document text/tree and per-file diagnostics.
    /// Includes parse diagnostics from the SyntaxTree plus all binding diagnostics
    /// from the compilation (which follows INCLUDE chains for full project context).
    /// </summary>
    public (SourceText? Text, SyntaxTree? Tree, IReadOnlyList<Diagnostic>? Diagnostics) GetDocumentDiagnostics(string uri)
    {
        Compilation? compilation;
        SourceText? text;
        SyntaxTree? tree;

        lock (_lock)
        {
            if (!_documents.TryGetValue(uri, out var doc))
                return (null, null, null);
            text = doc.Text;
            tree = doc.Tree;
            compilation = _compilation;
        }

        // Always include per-file parse diagnostics from the SyntaxTree
        var fileDiags = new List<Diagnostic>(tree.Diagnostics);

        if (compilation != null)
        {
            var model = GetOrCreateModel(compilation);
            foreach (var diag in model.Diagnostics)
                fileDiags.Add(diag);
        }

        return (text, tree, fileDiags);
    }

    public EmitModel? GetModel()
    {
        Compilation? compilation;
        lock (_lock) compilation = _compilation;
        if (compilation == null) return null;
        return GetOrCreateModel(compilation);
    }

    /// <summary>
    /// Get the semantic model for a specific document URI.
    /// Returns null if the document is not open or compilation is unavailable.
    /// </summary>
    public SemanticModel? GetSemanticModel(string uri)
    {
        Compilation? compilation;
        SyntaxTree? tree;
        lock (_lock)
        {
            if (!_documents.TryGetValue(uri, out var doc))
                return null;
            tree = doc.Tree;
            compilation = _compilation;
        }

        if (compilation == null) return null;
        return compilation.GetSemanticModel(tree);
    }

    public (SourceText Text, SyntaxTree Tree)? GetDocument(string uri)
    {
        lock (_lock)
            return _documents.TryGetValue(uri, out var doc) ? doc : null;
    }

    public IReadOnlyList<string> OpenDocumentUris
    {
        get { lock (_lock) return _documents.Keys.ToList(); }
    }

    private void RebuildCompilation()
    {
        // Perf: Compilation.Create is cheap (stores tree list only).
        // Binding is lazy, triggered on first Emit() call.
        _cachedModel = null;
        if (_documents.Count == 0)
        {
            _compilation = null;
            return;
        }
        var trees = _documents.Values.Select(d => d.Tree).ToArray();
        _compilation = Compilation.Create(trees);
    }

    private EmitModel GetOrCreateModel(Compilation compilation)
    {
        // Simple cache: if compilation hasn't changed, reuse the model
        var cached = _cachedModel;
        if (cached != null) return cached;
        var model = compilation.Emit();
        _cachedModel = model;
        return model;
    }
}
