using Koh.Core;
using Koh.Core.Syntax;
using Koh.Core.Text;
using Koh.Lsp.Config;
using Koh.Lsp.Discovery;
using Koh.Lsp.Source;

namespace Koh.Lsp.Projects;

/// <summary>
/// Manages project contexts for a single workspace folder. Maintains the include graph,
/// overlay resolver, entrypoint-to-project mapping, and file-to-primary-owner mapping.
/// Supports both configured (koh.yaml) and heuristic modes.
/// </summary>
internal sealed class ProjectContextManager
{
    private readonly WorkspaceOverlayResolver _overlayResolver;
    private readonly WorkspaceGraph _graph = new();
    private readonly IncludeDiscoveryService _includeDiscovery = new();
    private readonly string _folderPath;

    /// <summary>
    /// Set of file paths currently open in the editor. Used for heuristic entrypoint discovery.
    /// </summary>
    private readonly HashSet<string> _openFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// All files known to the workspace (discovered via overlays or graph).
    /// </summary>
    private readonly HashSet<string> _allFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Current folder mode (Configured, Heuristic, or InvalidConfiguration).
    /// </summary>
    public FolderMode Mode { get; private set; }

    /// <summary>
    /// Configured project definitions from koh.yaml (empty if heuristic or invalid).
    /// </summary>
    private IReadOnlyList<KohProjectDefinition> _configuredProjects = [];

    /// <summary>
    /// Configuration validation errors (populated only in InvalidConfiguration mode).
    /// </summary>
    public IReadOnlyList<ConfigValidationError> ConfigErrors { get; private set; } = [];

    /// <summary>
    /// Active project contexts keyed by entrypoint path.
    /// </summary>
    private readonly Dictionary<string, ProjectContext> _projects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Map from file path to its primary owner entrypoint path.
    /// </summary>
    private readonly Dictionary<string, string> _primaryOwner = new(StringComparer.OrdinalIgnoreCase);

    public ProjectContextManager(string folderPath, ISourceFileResolver innerResolver)
    {
        _folderPath = folderPath;
        _overlayResolver = new WorkspaceOverlayResolver(innerResolver);
    }

    /// <summary>
    /// Loads koh.yaml configuration or enters heuristic mode for the workspace folder.
    /// </summary>
    public void InitializeWorkspaceFolder(string folderPath)
    {
        var result = KohProjectFileLoader.Load(folderPath);
        ApplyConfig(result);
    }

    /// <summary>
    /// Applies a config load result directly (useful for testing without filesystem).
    /// </summary>
    internal void ApplyConfig(KohConfigLoadResult result)
    {
        switch (result)
        {
            case KohConfigLoadResult.Configured configured:
                Mode = FolderMode.Configured;
                _configuredProjects = configured.Projects;
                ConfigErrors = [];
                RebuildAllConfiguredProjects();
                break;

            case KohConfigLoadResult.Missing:
                Mode = FolderMode.Heuristic;
                _configuredProjects = [];
                ConfigErrors = [];
                RebuildHeuristicProjects();
                break;

            case KohConfigLoadResult.Invalid invalid:
                Mode = FolderMode.InvalidConfiguration;
                _configuredProjects = [];
                ConfigErrors = invalid.Errors;
                ClearAllProjects();
                break;
        }
    }

    /// <summary>
    /// Updates a document's overlay text, re-discovers includes, and rebuilds affected projects.
    /// </summary>
    public void UpdateDocumentText(string filePath, string text)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        _overlayResolver.SetOverlayText(normalizedPath, text);
        var isNewFile = _openFiles.Add(normalizedPath);
        _allFiles.Add(normalizedPath);

        // Capture old include edges before updating
        var oldIncludes = _graph.GetIncludes(normalizedPath);

        // Re-discover includes for this file
        var info = _includeDiscovery.Discover(normalizedPath, text, _folderPath);
        _graph.UpsertFile(info);

        // Track newly discovered included files
        foreach (var included in info.IncludedFiles)
        {
            _allFiles.Add(included);
        }

        if (Mode == FolderMode.InvalidConfiguration)
        {
            return;
        }

        if (Mode == FolderMode.Heuristic)
        {
            // If include edges changed or a new file was opened, the entrypoint set may
            // have changed — re-run full discovery. Otherwise just rebuild affected projects.
            var edgesChanged = isNewFile || !IncludeEdgesEqual(oldIncludes, info.IncludedFiles);
            if (edgesChanged)
            {
                RebuildHeuristicProjects();
            }
            else
            {
                RebuildAffectedProjects(normalizedPath);
            }
        }
        else
        {
            RebuildAffectedProjects(normalizedPath);
        }
    }

    private static bool IncludeEdgesEqual(IReadOnlySet<string> oldEdges, IReadOnlyList<string> newEdges)
    {
        if (oldEdges.Count != newEdges.Count)
            return false;

        foreach (var edge in newEdges)
        {
            if (!oldEdges.Contains(edge))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Removes a document's overlay and rebuilds affected projects.
    /// </summary>
    public void RemoveDocument(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        _overlayResolver.RemoveOverlay(normalizedPath);
        _openFiles.Remove(normalizedPath);

        // Remove from graph
        _graph.RemoveFile(normalizedPath);

        if (Mode == FolderMode.InvalidConfiguration)
        {
            return;
        }

        if (Mode == FolderMode.Heuristic)
        {
            _allFiles.Remove(normalizedPath);
            RebuildHeuristicProjects();
        }
        else
        {
            RebuildAffectedProjects(normalizedPath);
        }
    }

    /// <summary>
    /// Reloads koh.yaml configuration for the workspace folder.
    /// </summary>
    public void ReloadConfiguration(string folderPath)
    {
        InitializeWorkspaceFolder(folderPath);
    }

    /// <summary>
    /// Gets all project contexts that contain the given file.
    /// </summary>
    public IReadOnlyList<ProjectContext> GetProjectContextsFor(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        var result = new List<ProjectContext>();

        foreach (var project in _projects.Values)
        {
            if (project.ReachableFiles.Contains(normalizedPath))
            {
                result.Add(project);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the primary (deterministic) project context for a file, or null if none.
    /// </summary>
    public ProjectContext? GetPrimaryProjectContextFor(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);

        if (_primaryOwner.TryGetValue(normalizedPath, out var ownerEntrypoint)
            && _projects.TryGetValue(ownerEntrypoint, out var project))
        {
            return project;
        }

        return null;
    }

    /// <summary>
    /// Returns all active project contexts.
    /// </summary>
    public IReadOnlyCollection<ProjectContext> AllProjects => _projects.Values;

    /// <summary>
    /// Rebuilds only the project contexts affected by a change to the given file.
    /// In configured mode, finds which configured entrypoints can reach the changed file.
    /// </summary>
    internal void RebuildAffectedProjects(string changedFilePath)
    {
        var entrypoints = _projects.Keys;
        var affected = _graph.GetReachableEntrypoints(changedFilePath, entrypoints);

        foreach (var ep in affected)
        {
            if (_projects.TryGetValue(ep, out var existing))
            {
                var reachable = _graph.GetReachableFiles(ep);
                var compilation = BuildCompilation(ep);
                existing.Update(reachable, compilation);
            }
        }

        // If the changed file is itself a configured entrypoint not yet in projects, add it
        if (Mode == FolderMode.Configured)
        {
            foreach (var def in _configuredProjects)
            {
                if (string.Equals(def.Entrypoint, changedFilePath, StringComparison.OrdinalIgnoreCase)
                    && !_projects.ContainsKey(def.Entrypoint))
                {
                    BuildAndAddProject(def.Name, def.Entrypoint);
                }
            }
        }

        RebuildPrimaryOwnerMap();
    }

    private void RebuildAllConfiguredProjects()
    {
        _projects.Clear();
        _primaryOwner.Clear();

        foreach (var def in _configuredProjects)
        {
            BuildAndAddProject(def.Name, def.Entrypoint);
        }

        RebuildPrimaryOwnerMap();
    }

    private void RebuildHeuristicProjects()
    {
        var discoveryResult = EntrypointDiscoveryService.Discover(_graph, _allFiles, _openFiles);

        // Determine which entrypoints are new, removed, or unchanged
        var newEntrypoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in discoveryResult.Entrypoints)
        {
            newEntrypoints.Add(candidate.FilePath);
        }

        // Remove projects for entrypoints that are no longer candidates
        var toRemove = new List<string>();
        foreach (var ep in _projects.Keys)
        {
            if (!newEntrypoints.Contains(ep))
            {
                toRemove.Add(ep);
            }
        }

        foreach (var ep in toRemove)
        {
            _projects.Remove(ep);
        }

        // Add or rebuild projects
        foreach (var candidate in discoveryResult.Entrypoints)
        {
            var name = Path.GetFileNameWithoutExtension(candidate.FilePath);
            var reachable = _graph.GetReachableFiles(candidate.FilePath);
            var compilation = BuildCompilation(candidate.FilePath);

            if (_projects.TryGetValue(candidate.FilePath, out var existing))
            {
                existing.Update(reachable, compilation);
            }
            else
            {
                _projects[candidate.FilePath] = new ProjectContext(name, candidate.FilePath, reachable, compilation);
            }
        }

        // Use discovery result's ownership map for primary owner
        _primaryOwner.Clear();
        foreach (var (file, owner) in discoveryResult.FileOwnership)
        {
            _primaryOwner[file] = owner;
        }
    }

    private void BuildAndAddProject(string name, string entrypointPath)
    {
        // Ensure the include graph is populated for this entrypoint and all
        // transitive includes. Without this, files that were never opened in
        // the editor won't appear in the reachable set, and therefore won't
        // get a primary owner or receive semantic diagnostics.
        EnsureGraphPopulatedFromResolver(entrypointPath);

        var reachable = _graph.GetReachableFiles(entrypointPath);
        var compilation = BuildCompilation(entrypointPath);
        _projects[entrypointPath] = new ProjectContext(name, entrypointPath, reachable, compilation);
    }

    /// <summary>
    /// Recursively reads files from the overlay/disk resolver and discovers their
    /// INCLUDE directives, populating the workspace graph. This ensures that files
    /// which were never opened in the editor are still part of the include graph.
    /// </summary>
    private void EnsureGraphPopulatedFromResolver(string filePath)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        queue.Enqueue(filePath);
        visited.Add(filePath);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            // Skip if already scanned (via UpdateDocumentText or a previous call).
            // Leaf files with no includes are still marked as scanned, so we
            // don't re-read them on every call.
            if (_graph.IsScanned(current))
            {
                // Still need to traverse existing edges to find un-scanned descendants
                foreach (var existing in _graph.GetIncludes(current))
                {
                    if (visited.Add(existing))
                        queue.Enqueue(existing);
                }
                continue;
            }

            string text;
            try
            {
                text = _overlayResolver.ReadAllText(current);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            var info = _includeDiscovery.Discover(current, text, _folderPath);
            _graph.UpsertFile(info);
            _allFiles.Add(current);

            foreach (var included in info.IncludedFiles)
            {
                _allFiles.Add(included);
                if (visited.Add(included))
                    queue.Enqueue(included);
            }
        }
    }

    private Compilation BuildCompilation(string entrypointPath)
    {
        string text;
        try
        {
            text = _overlayResolver.ReadAllText(entrypointPath);
        }
        catch (FileNotFoundException)
        {
            // Entrypoint not yet on disk or in overlay — create empty compilation
            text = "";
        }

        var source = SourceText.From(text, entrypointPath);
        var tree = SyntaxTree.Parse(source);

        // Use the entrypoint's directory as the CWD for INCLUDE resolution.
        // RGBDS resolves includes relative to the build CWD, which is typically
        // the entrypoint's directory (e.g. src/ for src/game.asm).
        var entrypointDir = Path.GetDirectoryName(entrypointPath) ?? _folderPath;
        var resolver = new EntrypointResolver(_overlayResolver, entrypointDir);
        return Compilation.Create(resolver, tree);
    }

    /// <summary>
    /// Wraps the shared overlay resolver with per-entrypoint INCLUDE path resolution.
    /// Uses the entrypoint's directory as the CWD for resolving include paths,
    /// while delegating text/binary reads and overlay lookups to the shared resolver.
    /// </summary>
    private sealed class EntrypointResolver(ISourceFileResolver inner, string basePath) : ISourceFileResolver
    {
        public bool FileExists(string path) => inner.FileExists(path);
        public string ReadAllText(string path) => inner.ReadAllText(path);
        public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);

        public string ResolvePath(string currentFile, string includedPath)
        {
            // RGBDS behavior: resolve relative to CWD (entrypoint dir) first,
            // then relative to the including file's directory.
            var cwdPath = Path.GetFullPath(Path.Combine(basePath, includedPath));
            if (inner.FileExists(cwdPath)) return cwdPath;

            var dir = Path.GetDirectoryName(currentFile) ?? basePath;
            return Path.GetFullPath(Path.Combine(dir, includedPath));
        }
    }

    private void RebuildPrimaryOwnerMap()
    {
        _primaryOwner.Clear();

        // Collect all entrypoints sorted for deterministic assignment
        var sortedEntrypoints = _projects.Keys
            .OrderBy(ep => ep, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // For each file in each project, assign primary owner deterministically:
        // first entrypoint (alphabetically) that reaches this file wins
        foreach (var ep in sortedEntrypoints)
        {
            var project = _projects[ep];
            foreach (var file in project.ReachableFiles)
            {
                _primaryOwner.TryAdd(file, ep);
            }
        }
    }

    private void ClearAllProjects()
    {
        _projects.Clear();
        _primaryOwner.Clear();
    }
}
