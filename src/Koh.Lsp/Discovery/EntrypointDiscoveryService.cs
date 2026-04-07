namespace Koh.Lsp.Discovery;

/// <summary>
/// Confidence score for a candidate entrypoint, from highest to lowest.
/// </summary>
internal enum CandidateScore
{
    /// <summary>Not a candidate.</summary>
    None = 0,

    /// <summary>Directly opened .inc file — last-resort standalone analysis.</summary>
    OpenInc = 1,

    /// <summary>Standalone .asm file with no includes in or out.</summary>
    StandaloneAsm = 2,

    /// <summary>.asm file that is open and not included by a stronger candidate.</summary>
    OpenAsm = 3,

    /// <summary>.asm with outgoing includes and no incoming includes (true root).</summary>
    TrueRoot = 4,
}

/// <summary>
/// A candidate entrypoint with its confidence score.
/// </summary>
internal sealed record CandidateEntrypoint(string FilePath, CandidateScore Score);

/// <summary>
/// The result of entrypoint discovery: ranked candidates and file-to-owner mapping.
/// </summary>
internal sealed record DiscoveryResult(
    IReadOnlyList<CandidateEntrypoint> Entrypoints,
    IReadOnlyDictionary<string, string> FileOwnership);

/// <summary>
/// Discovers entrypoints in a workspace by analyzing the include graph.
/// Scores each file as a candidate entrypoint, then assigns ownership of every
/// file to a primary entrypoint using deterministic tie-breaking rules.
/// </summary>
internal static class EntrypointDiscoveryService
{
    /// <summary>
    /// Discovers entrypoints and computes file ownership from a populated workspace graph.
    /// </summary>
    /// <param name="graph">The include graph with all file relationships.</param>
    /// <param name="allFiles">All known file paths in the workspace.</param>
    /// <param name="openFiles">Files currently open in the editor.</param>
    public static DiscoveryResult Discover(
        WorkspaceGraph graph,
        IReadOnlySet<string> allFiles,
        IReadOnlySet<string> openFiles)
    {
        // Phase 1: Score every file as a candidate entrypoint.
        var candidates = new List<CandidateEntrypoint>();
        var scoreByFile = new Dictionary<string, CandidateScore>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in allFiles)
        {
            var score = ScoreCandidate(graph, file, openFiles);
            scoreByFile[file] = score;

            if (score > CandidateScore.None)
            {
                candidates.Add(new CandidateEntrypoint(file, score));
            }
        }

        // Sort candidates: highest score first, then alphabetically for stability.
        candidates.Sort((a, b) =>
        {
            var cmp = b.Score.CompareTo(a.Score);
            return cmp != 0 ? cmp : string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
        });

        // Phase 2: BFS from each entrypoint to compute distances.
        var entrypointPaths = candidates.Select(c => c.FilePath).ToList();
        var distanceFromEntrypoint = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ep in entrypointPaths)
        {
            distanceFromEntrypoint[ep] = BfsDistances(graph, ep);
        }

        // Phase 3: For each file, pick a primary owner.
        var ownership = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in allFiles)
        {
            string? bestOwner = null;
            var bestScore = CandidateScore.None;
            var bestDistance = int.MaxValue;

            foreach (var ep in entrypointPaths)
            {
                if (!distanceFromEntrypoint[ep].TryGetValue(file, out var distance))
                    continue;

                var epScore = scoreByFile[ep];

                if (epScore > bestScore
                    || (epScore == bestScore && distance < bestDistance)
                    || (epScore == bestScore && distance == bestDistance
                        && string.Compare(ep, bestOwner, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    bestOwner = ep;
                    bestScore = epScore;
                    bestDistance = distance;
                }
            }

            if (bestOwner is not null)
            {
                ownership[file] = bestOwner;
            }
        }

        return new DiscoveryResult(candidates, ownership);
    }

    private static CandidateScore ScoreCandidate(
        WorkspaceGraph graph,
        string filePath,
        IReadOnlySet<string> openFiles)
    {
        var isAsm = filePath.EndsWith(".asm", StringComparison.OrdinalIgnoreCase);
        var hasOutgoing = graph.GetIncludes(filePath).Count > 0;
        var hasIncoming = graph.GetIncluders(filePath).Count > 0;
        var isOpen = openFiles.Contains(filePath);

        if (isAsm && hasOutgoing && !hasIncoming)
            return CandidateScore.TrueRoot;

        if (isAsm && isOpen && !hasIncoming)
            return CandidateScore.OpenAsm;

        if (isAsm && !hasOutgoing && !hasIncoming)
            return CandidateScore.StandaloneAsm;

        if (!isAsm && isOpen && !hasIncoming)
            return CandidateScore.OpenInc;

        return CandidateScore.None;
    }

    /// <summary>
    /// BFS from a start file following forward (include) edges, returning the distance to each reachable file.
    /// </summary>
    private static Dictionary<string, int> BfsDistances(WorkspaceGraph graph, string start)
    {
        var distances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [start] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentDist = distances[current];

            foreach (var neighbor in graph.GetIncludes(current))
            {
                if (!distances.ContainsKey(neighbor))
                {
                    distances[neighbor] = currentDist + 1;
                    queue.Enqueue(neighbor);
                }
            }
        }

        return distances;
    }
}
