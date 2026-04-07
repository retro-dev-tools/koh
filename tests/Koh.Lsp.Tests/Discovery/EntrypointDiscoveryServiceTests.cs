using Koh.Lsp.Discovery;

namespace Koh.Lsp.Tests.Discovery;

public class EntrypointDiscoveryServiceTests
{
    private readonly WorkspaceGraph _graph = new();

    [Test]
    public async Task SingleEntrypoint_TrueRoot_IsDiscovered()
    {
        // main.asm includes utils.inc — main.asm is a true root
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/utils.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/utils.inc", []));

        var allFiles = ToSet("C:/project/main.asm", "C:/project/utils.inc");
        var openFiles = ToSet<string>();

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        await Assert.That(result.Entrypoints.Count).IsEqualTo(1);
        await Assert.That(result.Entrypoints[0].FilePath).IsEqualTo("C:/project/main.asm");
        await Assert.That(result.Entrypoints[0].Score).IsEqualTo(CandidateScore.TrueRoot);
    }

    [Test]
    public async Task MultiEntrypoint_BothDiscovered_CorrectOrder()
    {
        // Two independent roots
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/game.asm", ["C:/project/shared.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/tools.asm", ["C:/project/shared.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/shared.inc", []));

        var allFiles = ToSet("C:/project/game.asm", "C:/project/tools.asm", "C:/project/shared.inc");
        var openFiles = ToSet<string>();

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        await Assert.That(result.Entrypoints.Count).IsEqualTo(2);
        // Both should be TrueRoot, sorted alphabetically
        await Assert.That(result.Entrypoints[0].FilePath).IsEqualTo("C:/project/game.asm");
        await Assert.That(result.Entrypoints[1].FilePath).IsEqualTo("C:/project/tools.asm");
        await Assert.That(result.Entrypoints[0].Score).IsEqualTo(CandidateScore.TrueRoot);
        await Assert.That(result.Entrypoints[1].Score).IsEqualTo(CandidateScore.TrueRoot);
    }

    [Test]
    public async Task StandaloneAsm_NoIncludes_IsCandidate()
    {
        // standalone.asm has no includes in or out
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/standalone.asm", []));

        var allFiles = ToSet("C:/project/standalone.asm");
        var openFiles = ToSet<string>();

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        await Assert.That(result.Entrypoints.Count).IsEqualTo(1);
        await Assert.That(result.Entrypoints[0].Score).IsEqualTo(CandidateScore.StandaloneAsm);
    }

    [Test]
    public async Task DirectOpenInc_StandaloneFallback()
    {
        // An .inc file that is open and not included by anything
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/macros.inc", []));

        var allFiles = ToSet("C:/project/macros.inc");
        var openFiles = ToSet("C:/project/macros.inc");

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        await Assert.That(result.Entrypoints.Count).IsEqualTo(1);
        await Assert.That(result.Entrypoints[0].FilePath).IsEqualTo("C:/project/macros.inc");
        await Assert.That(result.Entrypoints[0].Score).IsEqualTo(CandidateScore.OpenInc);
    }

    [Test]
    public async Task SharedInclude_OwnedByHighestScoringEntrypoint()
    {
        // game.asm (TrueRoot) and tools.asm (TrueRoot) both include shared.inc
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/game.asm", ["C:/project/shared.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/tools.asm", ["C:/project/shared.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/shared.inc", []));

        var allFiles = ToSet("C:/project/game.asm", "C:/project/tools.asm", "C:/project/shared.inc");
        var openFiles = ToSet<string>();

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        // Both entrypoints have same score and same distance (1) to shared.inc
        // Tie-break: alphabetically first path wins → game.asm
        await Assert.That(result.FileOwnership["C:/project/shared.inc"]).IsEqualTo("C:/project/game.asm");
    }

    [Test]
    public async Task DeterministicPrimaryOwner_ShortestDistance_Wins()
    {
        // entry1.asm -> mid.asm -> deep.inc
        // entry2.asm -> deep.inc  (shorter path)
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/entry1.asm", ["C:/project/mid.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/mid.asm", ["C:/project/deep.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/entry2.asm", ["C:/project/deep.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/deep.inc", []));

        var allFiles = ToSet("C:/project/entry1.asm", "C:/project/entry2.asm", "C:/project/mid.asm", "C:/project/deep.inc");
        var openFiles = ToSet<string>();

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        // Both are TrueRoot (same score). entry2 has distance 1 to deep.inc, entry1 has distance 2.
        // Shortest distance wins → entry2.asm owns deep.inc
        await Assert.That(result.FileOwnership["C:/project/deep.inc"]).IsEqualTo("C:/project/entry2.asm");
    }

    [Test]
    public async Task DeterministicPrimaryOwner_AlphabeticTieBreaker()
    {
        // Two entrypoints at equal score and equal distance — alphabetic path wins
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/beta.asm", ["C:/project/shared.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/alpha.asm", ["C:/project/shared.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/shared.inc", []));

        var allFiles = ToSet("C:/project/alpha.asm", "C:/project/beta.asm", "C:/project/shared.inc");
        var openFiles = ToSet<string>();

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        await Assert.That(result.FileOwnership["C:/project/shared.inc"]).IsEqualTo("C:/project/alpha.asm");
    }

    [Test]
    public async Task CycleHandling_DoesNotInfiniteLoop()
    {
        // a.asm -> b.asm -> a.asm (cycle)
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/a.asm", ["C:/project/b.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/b.asm", ["C:/project/a.asm"]));

        var allFiles = ToSet("C:/project/a.asm", "C:/project/b.asm");
        var openFiles = ToSet<string>();

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        // Neither has no incoming includes (both include each other), so neither is TrueRoot.
        // Neither is open, so not OpenAsm. Neither is standalone (both have edges).
        // No candidates expected.
        await Assert.That(result.Entrypoints.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CycleHandling_WithOpenFile_BreaksTie()
    {
        // a.asm -> b.asm -> a.asm (cycle), but a.asm is open
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/a.asm", ["C:/project/b.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/b.asm", ["C:/project/a.asm"]));

        var allFiles = ToSet("C:/project/a.asm", "C:/project/b.asm");
        var openFiles = ToSet("C:/project/a.asm");

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        // Both have incoming includes, so neither is TrueRoot or StandaloneAsm.
        // a.asm is open but has incoming includes → not OpenAsm either.
        // No candidates.
        await Assert.That(result.Entrypoints.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EntrypointOwnsItself()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/utils.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/utils.inc", []));

        var allFiles = ToSet("C:/project/main.asm", "C:/project/utils.inc");
        var openFiles = ToSet<string>();

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        await Assert.That(result.FileOwnership["C:/project/main.asm"]).IsEqualTo("C:/project/main.asm");
        await Assert.That(result.FileOwnership["C:/project/utils.inc"]).IsEqualTo("C:/project/main.asm");
    }

    [Test]
    public async Task IncFile_IncludedByAsm_NotACandidate()
    {
        // .inc file that is included by an .asm — should not be a candidate even if open
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/defs.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/defs.inc", []));

        var allFiles = ToSet("C:/project/main.asm", "C:/project/defs.inc");
        var openFiles = ToSet("C:/project/defs.inc");

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        // Only main.asm should be a candidate (TrueRoot)
        await Assert.That(result.Entrypoints.Count).IsEqualTo(1);
        await Assert.That(result.Entrypoints[0].FilePath).IsEqualTo("C:/project/main.asm");
    }

    [Test]
    public async Task OpenAsm_NotIncludedByStrongerCandidate()
    {
        // open.asm is open and not included by anything
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/open.asm", []));

        var allFiles = ToSet("C:/project/open.asm");
        var openFiles = ToSet("C:/project/open.asm");

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        // Standalone .asm with no edges is StandaloneAsm (no outgoing includes for OpenAsm→TrueRoot upgrade)
        // Actually: isAsm=true, hasOutgoing=false, hasIncoming=false, isOpen=true
        // First check: isAsm && hasOutgoing && !hasIncoming → false (no outgoing)
        // Second check: isAsm && isOpen && !hasIncoming → true → OpenAsm
        await Assert.That(result.Entrypoints[0].Score).IsEqualTo(CandidateScore.OpenAsm);
    }

    [Test]
    public async Task MixedScores_HigherScoreWinsOwnership()
    {
        // root.asm (TrueRoot) -> shared.inc
        // open.asm (OpenAsm, open but no includes) — standalone, not connected to shared.inc
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/root.asm", ["C:/project/shared.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/open.asm", []));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/shared.inc", []));

        var allFiles = ToSet("C:/project/root.asm", "C:/project/open.asm", "C:/project/shared.inc");
        var openFiles = ToSet("C:/project/open.asm");

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        // root.asm is TrueRoot (score 4), open.asm is OpenAsm (score 3)
        await Assert.That(result.Entrypoints[0].Score).IsEqualTo(CandidateScore.TrueRoot);
        await Assert.That(result.Entrypoints[0].FilePath).IsEqualTo("C:/project/root.asm");

        // shared.inc is only reachable from root.asm
        await Assert.That(result.FileOwnership["C:/project/shared.inc"]).IsEqualTo("C:/project/root.asm");
    }

    [Test]
    public async Task FileNotReachableByAnyEntrypoint_NotInOwnership()
    {
        // isolated.inc has no edges and is not open — not a candidate, not reachable
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/utils.inc"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/utils.inc", []));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/isolated.inc", []));

        var allFiles = ToSet("C:/project/main.asm", "C:/project/utils.inc", "C:/project/isolated.inc");
        var openFiles = ToSet<string>();

        var result = EntrypointDiscoveryService.Discover(_graph, allFiles, openFiles);

        await Assert.That(result.FileOwnership.ContainsKey("C:/project/isolated.inc")).IsFalse();
    }

    private static HashSet<string> ToSet(params string[] items)
    {
        return new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ToSet<T>()
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
