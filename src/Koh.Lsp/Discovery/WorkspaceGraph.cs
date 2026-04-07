namespace Koh.Lsp.Discovery;

/// <summary>
/// Tracks include relationships between workspace files as a directed graph.
/// Supports forward edges (A includes B), reverse edges (B is included by A),
/// and reachability queries in both directions. All paths are case-insensitive.
/// </summary>
internal sealed class WorkspaceGraph
{
    private readonly Dictionary<string, HashSet<string>> _forwardEdges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _reverseEdges = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds or updates a file's include edges. Old edges for the file are removed first.
    /// </summary>
    public void UpsertFile(FileDiscoveryInfo info)
    {
        // Remove old forward edges for this file and their corresponding reverse entries
        RemoveForwardEdges(info.FilePath);

        // Add new forward edges
        var targets = GetOrCreateSet(_forwardEdges, info.FilePath);
        foreach (var included in info.IncludedFiles)
        {
            targets.Add(included);
            GetOrCreateSet(_reverseEdges, included).Add(info.FilePath);
        }
    }

    /// <summary>
    /// Removes a file and all its edges (both forward and reverse).
    /// </summary>
    public void RemoveFile(string path)
    {
        // Remove forward edges (this file includes X)
        RemoveForwardEdges(path);
        _forwardEdges.Remove(path);

        // Remove reverse edges (X includes this file) — clean up the forward side too
        if (_reverseEdges.TryGetValue(path, out var includers))
        {
            foreach (var includer in includers)
            {
                if (_forwardEdges.TryGetValue(includer, out var includerTargets))
                {
                    includerTargets.Remove(path);
                }
            }

            _reverseEdges.Remove(path);
        }
    }

    /// <summary>
    /// Gets the files that this file includes (forward edges).
    /// </summary>
    public IReadOnlySet<string> GetIncludes(string path)
    {
        return _forwardEdges.TryGetValue(path, out var targets) ? targets : EmptySet;
    }

    /// <summary>
    /// Gets the files that include this file (reverse edges).
    /// </summary>
    public IReadOnlySet<string> GetIncluders(string path)
    {
        return _reverseEdges.TryGetValue(path, out var includers) ? includers : EmptySet;
    }

    /// <summary>
    /// Returns all files reachable from the given entrypoint by following forward (include) edges.
    /// The entrypoint itself is included in the result. Handles cycles with a visited set.
    /// </summary>
    public IReadOnlySet<string> GetReachableFiles(string entrypointPath)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        visited.Add(entrypointPath);
        queue.Enqueue(entrypointPath);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (_forwardEdges.TryGetValue(current, out var targets))
            {
                foreach (var target in targets)
                {
                    if (visited.Add(target))
                    {
                        queue.Enqueue(target);
                    }
                }
            }
        }

        return visited;
    }

    /// <summary>
    /// Given a file and candidate entrypoints, returns which entrypoints can reach this file
    /// by following forward edges. Uses reverse BFS from the file to find reachable ancestors,
    /// then intersects with the candidate set.
    /// </summary>
    public IReadOnlySet<string> GetReachableEntrypoints(string filePath, IEnumerable<string> entrypoints)
    {
        // BFS backwards from filePath following reverse edges
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();

        visited.Add(filePath);
        queue.Enqueue(filePath);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (_reverseEdges.TryGetValue(current, out var includers))
            {
                foreach (var includer in includers)
                {
                    if (visited.Add(includer))
                    {
                        queue.Enqueue(includer);
                    }
                }
            }
        }

        // Intersect with candidate entrypoints
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ep in entrypoints)
        {
            if (visited.Contains(ep))
            {
                result.Add(ep);
            }
        }

        return result;
    }

    private void RemoveForwardEdges(string path)
    {
        if (_forwardEdges.TryGetValue(path, out var oldTargets))
        {
            foreach (var target in oldTargets)
            {
                if (_reverseEdges.TryGetValue(target, out var reverseSet))
                {
                    reverseSet.Remove(path);
                }
            }

            oldTargets.Clear();
        }
    }

    private static HashSet<string> GetOrCreateSet(Dictionary<string, HashSet<string>> dict, string key)
    {
        if (!dict.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dict[key] = set;
        }

        return set;
    }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
