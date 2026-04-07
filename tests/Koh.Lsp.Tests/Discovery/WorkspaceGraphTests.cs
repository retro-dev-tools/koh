using Koh.Lsp.Discovery;

namespace Koh.Lsp.Tests.Discovery;

public class WorkspaceGraphTests
{
    private readonly WorkspaceGraph _graph = new();

    [Test]
    public async Task UpsertFile_AddsForwardEdges()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/utils.asm", "C:/project/data.asm"]));

        var includes = _graph.GetIncludes("C:/project/main.asm");

        await Assert.That(includes.Count).IsEqualTo(2);
        await Assert.That(includes.Contains("C:/project/utils.asm")).IsTrue();
        await Assert.That(includes.Contains("C:/project/data.asm")).IsTrue();
    }

    [Test]
    public async Task UpsertFile_AddsReverseEdges()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/utils.asm"]));

        var includers = _graph.GetIncluders("C:/project/utils.asm");

        await Assert.That(includers.Count).IsEqualTo(1);
        await Assert.That(includers.Contains("C:/project/main.asm")).IsTrue();
    }

    [Test]
    public async Task UpsertFile_ReplacesOldEdges()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/old.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/new.asm"]));

        var includes = _graph.GetIncludes("C:/project/main.asm");

        await Assert.That(includes.Count).IsEqualTo(1);
        await Assert.That(includes.Contains("C:/project/new.asm")).IsTrue();
        await Assert.That(includes.Contains("C:/project/old.asm")).IsFalse();
    }

    [Test]
    public async Task UpsertFile_RemovesStaleReverseEdges()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/old.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/new.asm"]));

        var oldIncluders = _graph.GetIncluders("C:/project/old.asm");
        var newIncluders = _graph.GetIncluders("C:/project/new.asm");

        await Assert.That(oldIncluders.Count).IsEqualTo(0);
        await Assert.That(newIncluders.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RemoveFile_RemovesForwardAndReverseEdges()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/utils.asm"]));
        _graph.RemoveFile("C:/project/main.asm");

        var includes = _graph.GetIncludes("C:/project/main.asm");
        var includers = _graph.GetIncluders("C:/project/utils.asm");

        await Assert.That(includes.Count).IsEqualTo(0);
        await Assert.That(includers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RemoveFile_RemovesIncomingEdgesFromOtherFiles()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/utils.asm"]));
        _graph.RemoveFile("C:/project/utils.asm");

        var includes = _graph.GetIncludes("C:/project/main.asm");

        await Assert.That(includes.Contains("C:/project/utils.asm")).IsFalse();
    }

    [Test]
    public async Task RemoveFile_NonExistentFile_DoesNotThrow()
    {
        _graph.RemoveFile("C:/project/nonexistent.asm");

        var includes = _graph.GetIncludes("C:/project/nonexistent.asm");
        await Assert.That(includes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetIncludes_UnknownFile_ReturnsEmpty()
    {
        var includes = _graph.GetIncludes("C:/project/unknown.asm");

        await Assert.That(includes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetIncluders_UnknownFile_ReturnsEmpty()
    {
        var includers = _graph.GetIncluders("C:/project/unknown.asm");

        await Assert.That(includers.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetReachableFiles_SingleFile_ReturnsSelf()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", []));

        var reachable = _graph.GetReachableFiles("C:/project/main.asm");

        await Assert.That(reachable.Count).IsEqualTo(1);
        await Assert.That(reachable.Contains("C:/project/main.asm")).IsTrue();
    }

    [Test]
    public async Task GetReachableFiles_Chain_ReturnsAll()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/a.asm", ["C:/project/b.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/b.asm", ["C:/project/c.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/c.asm", []));

        var reachable = _graph.GetReachableFiles("C:/project/a.asm");

        await Assert.That(reachable.Count).IsEqualTo(3);
        await Assert.That(reachable.Contains("C:/project/a.asm")).IsTrue();
        await Assert.That(reachable.Contains("C:/project/b.asm")).IsTrue();
        await Assert.That(reachable.Contains("C:/project/c.asm")).IsTrue();
    }

    [Test]
    public async Task GetReachableFiles_Cycle_DoesNotInfiniteLoop()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/a.asm", ["C:/project/b.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/b.asm", ["C:/project/c.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/c.asm", ["C:/project/a.asm"]));

        var reachable = _graph.GetReachableFiles("C:/project/a.asm");

        await Assert.That(reachable.Count).IsEqualTo(3);
    }

    [Test]
    public async Task GetReachableFiles_Diamond_ReturnsAllOnce()
    {
        // A -> B, A -> C, B -> D, C -> D
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/a.asm", ["C:/project/b.asm", "C:/project/c.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/b.asm", ["C:/project/d.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/c.asm", ["C:/project/d.asm"]));

        var reachable = _graph.GetReachableFiles("C:/project/a.asm");

        await Assert.That(reachable.Count).IsEqualTo(4);
    }

    [Test]
    public async Task GetReachableEntrypoints_FindsCorrectEntrypoint()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/utils.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/utils.asm", ["C:/project/math.asm"]));

        var entrypoints = _graph.GetReachableEntrypoints(
            "C:/project/math.asm",
            ["C:/project/main.asm", "C:/project/other.asm"]);

        await Assert.That(entrypoints.Count).IsEqualTo(1);
        await Assert.That(entrypoints.Contains("C:/project/main.asm")).IsTrue();
    }

    [Test]
    public async Task GetReachableEntrypoints_FileIsItsOwnEntrypoint()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", []));

        var entrypoints = _graph.GetReachableEntrypoints(
            "C:/project/main.asm",
            ["C:/project/main.asm"]);

        await Assert.That(entrypoints.Count).IsEqualTo(1);
        await Assert.That(entrypoints.Contains("C:/project/main.asm")).IsTrue();
    }

    [Test]
    public async Task GetReachableEntrypoints_NoMatchingEntrypoint_ReturnsEmpty()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/main.asm", ["C:/project/utils.asm"]));

        var entrypoints = _graph.GetReachableEntrypoints(
            "C:/project/isolated.asm",
            ["C:/project/main.asm"]);

        await Assert.That(entrypoints.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetReachableEntrypoints_Cycle_DoesNotInfiniteLoop()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/a.asm", ["C:/project/b.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/b.asm", ["C:/project/a.asm"]));

        var entrypoints = _graph.GetReachableEntrypoints(
            "C:/project/a.asm",
            ["C:/project/a.asm", "C:/project/b.asm"]);

        await Assert.That(entrypoints.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CaseInsensitivePaths()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/Project/Main.asm", ["C:/Project/Utils.asm"]));

        var includes = _graph.GetIncludes("c:/project/main.asm");
        var includers = _graph.GetIncluders("c:/project/utils.asm");

        await Assert.That(includes.Count).IsEqualTo(1);
        await Assert.That(includers.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MultipleIncluders_AllTracked()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/a.asm", ["C:/project/shared.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/b.asm", ["C:/project/shared.asm"]));

        var includers = _graph.GetIncluders("C:/project/shared.asm");

        await Assert.That(includers.Count).IsEqualTo(2);
        await Assert.That(includers.Contains("C:/project/a.asm")).IsTrue();
        await Assert.That(includers.Contains("C:/project/b.asm")).IsTrue();
    }

    [Test]
    public async Task GetReachableEntrypoints_MultipleEntrypointsReachFile()
    {
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/entry1.asm", ["C:/project/shared.asm"]));
        _graph.UpsertFile(new FileDiscoveryInfo("C:/project/entry2.asm", ["C:/project/shared.asm"]));

        var entrypoints = _graph.GetReachableEntrypoints(
            "C:/project/shared.asm",
            ["C:/project/entry1.asm", "C:/project/entry2.asm"]);

        await Assert.That(entrypoints.Count).IsEqualTo(2);
    }
}
