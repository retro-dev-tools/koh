using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax;
using Koh.Core.Text;
using Koh.Lsp.Config;
using Koh.Lsp.Projects;

namespace Koh.Lsp;

/// <summary>
/// Holds open documents and maintains live compilations via project contexts.
/// Thread-safe: all mutations go through a lock.
///
/// In "initialized" mode (after <see cref="InitializeFolder"/> is called),
/// the workspace delegates compilation ownership to a <see cref="ProjectContextManager"/>.
/// In "standalone" mode (no folder initialized), it falls back to the legacy behavior
/// of creating one compilation from all open documents.
/// </summary>
internal sealed class Workspace
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (SourceText Text, SyntaxTree Tree)> _documents = new(StringComparer.OrdinalIgnoreCase);

    // --- Legacy standalone mode fields ---
    private Compilation? _standaloneCompilation;
    private volatile EmitModel? _standaloneCachedModel;

    // --- Project-context-aware mode ---
    private ProjectContextManager? _projectContextManager;
    private string? _folderPath;

    /// <summary>
    /// Initializes a workspace folder, creating a <see cref="ProjectContextManager"/>
    /// that handles configuration loading and entrypoint discovery.
    /// </summary>
    public void InitializeFolder(string folderPath, ISourceFileResolver? innerResolver = null)
    {
        lock (_lock)
        {
            _folderPath = folderPath;
            _projectContextManager = new ProjectContextManager(
                folderPath,
                innerResolver ?? new FileSystemResolver());
            _projectContextManager.InitializeWorkspaceFolder(folderPath);

            // Feed any already-open documents into the project context manager
            foreach (var (path, doc) in _documents)
            {
                _projectContextManager.UpdateDocumentText(path, doc.Text.ToString());
            }

            // Clear standalone compilation since we now have project contexts
            _standaloneCompilation = null;
            _standaloneCachedModel = null;
        }
    }

    /// <summary>
    /// Reloads koh.yaml configuration for the workspace folder.
    /// </summary>
    public void ReloadConfiguration(string folderPath)
    {
        lock (_lock)
        {
            _projectContextManager?.ReloadConfiguration(folderPath);
        }
    }

    public void OpenDocument(string path, string text)
    {
        lock (_lock)
        {
            var source = SourceText.From(text, path);
            var tree = SyntaxTree.Parse(source);
            _documents[path] = (source, tree);

            if (_projectContextManager != null)
            {
                _projectContextManager.UpdateDocumentText(path, text);
            }
            else
            {
                RebuildStandaloneCompilation();
            }
        }
    }

    public void ChangeDocument(string path, string text)
    {
        lock (_lock)
        {
            var source = SourceText.From(text, path);

            // Try incremental reparse when we have a previous tree for this document.
            SyntaxTree tree;
            if (_documents.TryGetValue(path, out var prev))
            {
                tree = prev.Tree.WithChanges(source);
            }
            else
            {
                tree = SyntaxTree.Parse(source);
            }

            _documents[path] = (source, tree);

            if (_projectContextManager != null)
            {
                _projectContextManager.UpdateDocumentText(path, text);
            }
            else
            {
                RebuildStandaloneCompilation();
            }
        }
    }

    public void CloseDocument(string uri)
    {
        lock (_lock)
        {
            _documents.Remove(uri);

            if (_projectContextManager != null)
            {
                _projectContextManager.RemoveDocument(uri);
            }
            else
            {
                RebuildStandaloneCompilation();
            }
        }
    }

    /// <summary>
    /// Get document text/tree and per-file diagnostics.
    /// In project-context mode: parse diagnostics from the file's own tree,
    /// plus semantic diagnostics from the primary project context filtered to this file.
    /// Diagnostics with null FilePath are attached only to the entrypoint document.
    /// In standalone mode: all compilation diagnostics (legacy behavior).
    /// </summary>
    public (SourceText? Text, SyntaxTree? Tree, IReadOnlyList<Diagnostic>? Diagnostics) GetDocumentDiagnostics(string uri)
    {
        ProjectContextManager? pcm;
        Compilation? standaloneCompilation;
        SourceText? text;
        SyntaxTree? tree;

        lock (_lock)
        {
            if (!_documents.TryGetValue(uri, out var doc))
                return (null, null, null);
            text = doc.Text;
            tree = doc.Tree;
            pcm = _projectContextManager;
            standaloneCompilation = _standaloneCompilation;
        }

        // Always include per-file parse diagnostics from the SyntaxTree
        var fileDiags = new List<Diagnostic>(tree.Diagnostics);

        if (pcm != null)
        {
            // Project-context mode: get diagnostics from primary project context
            var context = pcm.GetPrimaryProjectContextFor(uri);
            if (context != null)
            {
                var emitModel = context.Compilation.Emit();
                foreach (var diag in emitModel.Diagnostics)
                {
                    if (diag.FilePath == null)
                    {
                        // Unattributed diagnostics — only attach to the entrypoint document
                        if (string.Equals(uri, context.EntrypointPath, StringComparison.OrdinalIgnoreCase))
                            fileDiags.Add(diag);
                    }
                    else if (string.Equals(diag.FilePath, uri, StringComparison.OrdinalIgnoreCase))
                    {
                        fileDiags.Add(diag);
                    }
                }
            }
            // If no project context owns this file, only syntax diagnostics are reported (standalone analysis)
        }
        else if (standaloneCompilation != null)
        {
            // Legacy standalone mode: add all compilation diagnostics
            var model = GetOrCreateStandaloneModel(standaloneCompilation);
            foreach (var diag in model.Diagnostics)
                fileDiags.Add(diag);
        }

        return (text, tree, fileDiags);
    }

    /// <summary>
    /// Gets the emit model.
    /// In project-context mode: returns the primary project context's emit model for the first open document,
    /// or null if no projects exist.
    /// In standalone mode: returns the single compilation's emit model.
    /// </summary>
    public EmitModel? GetModel()
    {
        lock (_lock)
        {
            if (_projectContextManager != null)
            {
                // Return the first available project's emit model
                foreach (var project in _projectContextManager.AllProjects)
                {
                    return project.Compilation.Emit();
                }
                return null;
            }

            if (_standaloneCompilation == null) return null;
            return GetOrCreateStandaloneModel(_standaloneCompilation);
        }
    }

    /// <summary>
    /// Gets the emit model for a specific file's primary project context.
    /// </summary>
    public EmitModel? GetModel(string uri)
    {
        lock (_lock)
        {
            if (_projectContextManager != null)
            {
                var context = _projectContextManager.GetPrimaryProjectContextFor(uri);
                return context?.Compilation.Emit();
            }

            if (_standaloneCompilation == null) return null;
            return GetOrCreateStandaloneModel(_standaloneCompilation);
        }
    }

    /// <summary>
    /// Get the semantic model for a specific document URI.
    /// In project-context mode: uses the primary project context's compilation.
    /// Since compilations only contain the entrypoint tree, we return the entrypoint's
    /// semantic model which contains all resolved symbols from the include chain.
    /// In standalone mode: returns the semantic model from the standalone compilation.
    /// </summary>
    public SemanticModel? GetSemanticModel(string uri)
    {
        lock (_lock)
        {
            if (_projectContextManager != null)
            {
                var context = _projectContextManager.GetPrimaryProjectContextFor(uri);
                if (context == null) return null;

                // The compilation has only the entrypoint tree. The binder resolves
                // INCLUDE chains internally, so the entrypoint's semantic model
                // contains all symbols from the include chain.
                var entrypointTree = context.Compilation.SyntaxTrees[0];
                return context.Compilation.GetSemanticModel(entrypointTree);
            }

            // Legacy standalone mode
            if (!_documents.TryGetValue(uri, out var doc))
                return null;
            if (_standaloneCompilation == null) return null;
            return _standaloneCompilation.GetSemanticModel(doc.Tree);
        }
    }

    /// <summary>
    /// Gets the primary project context for a file, or null if not in project-context mode
    /// or the file has no owning project.
    /// </summary>
    public ProjectContext? GetPrimaryProjectContext(string uri)
    {
        lock (_lock)
        {
            return _projectContextManager?.GetPrimaryProjectContextFor(uri);
        }
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

    /// <summary>
    /// Whether this workspace has been initialized with a folder (project-context mode).
    /// </summary>
    public bool IsInitialized
    {
        get { lock (_lock) return _projectContextManager != null; }
    }

    /// <summary>
    /// The current folder mode, or null if not initialized.
    /// </summary>
    public FolderMode? CurrentMode
    {
        get { lock (_lock) return _projectContextManager?.Mode; }
    }

    // --- Standalone mode helpers (legacy, for when no folder is initialized) ---

    private void RebuildStandaloneCompilation()
    {
        _standaloneCachedModel = null;
        if (_documents.Count == 0)
        {
            _standaloneCompilation = null;
            return;
        }
        var trees = _documents.Values.Select(d => d.Tree).ToArray();
        _standaloneCompilation = Compilation.Create(trees);
    }

    private EmitModel GetOrCreateStandaloneModel(Compilation compilation)
    {
        var cached = _standaloneCachedModel;
        if (cached != null) return cached;
        var model = compilation.Emit();
        _standaloneCachedModel = model;
        return model;
    }
}
