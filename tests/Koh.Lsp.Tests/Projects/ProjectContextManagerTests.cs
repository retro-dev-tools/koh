using Koh.Core;
using Koh.Lsp.Config;
using Koh.Lsp.Discovery;
using Koh.Lsp.Projects;
using Koh.Lsp.Source;

namespace Koh.Lsp.Tests.Projects;

public class ProjectContextManagerTests
{
    private const string FolderPath = "C:/project";

    private static ProjectContextManager CreateManager(VirtualFileResolver? inner = null)
    {
        return new ProjectContextManager(FolderPath, inner ?? new VirtualFileResolver());
    }

    // ───────────────────────────────────────────────────────────────
    // Task 6.8a: One compilation per entrypoint
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task OneCompilationPerEntrypoint_TwoEntrypoints_ProduceTwoProjects()
    {
        var manager = CreateManager();

        // Two independent entrypoints, each including a shared file
        manager.ApplyConfig(new KohConfigLoadResult.Missing());

        manager.UpdateDocumentText("C:/project/game.asm", "INCLUDE \"shared.inc\"\nnop");
        manager.UpdateDocumentText("C:/project/tools.asm", "INCLUDE \"shared.inc\"\nhalt");
        manager.UpdateDocumentText("C:/project/shared.inc", "; shared utilities");

        var projects = manager.AllProjects;
        await Assert.That(projects.Count).IsEqualTo(2);

        // Each project has its own compilation instance
        var compilations = projects.Select(p => p.Compilation).ToList();
        await Assert.That(compilations[0]).IsNotEqualTo(compilations[1]);
    }

    [Test]
    public async Task OneCompilationPerEntrypoint_EachHasCorrectEntrypoint()
    {
        var manager = CreateManager();
        manager.ApplyConfig(new KohConfigLoadResult.Missing());

        manager.UpdateDocumentText("C:/project/main.asm", "nop");
        manager.UpdateDocumentText("C:/project/test.asm", "halt");

        var projects = manager.AllProjects;
        var entrypoints = projects.Select(p => p.EntrypointPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        await Assert.That(entrypoints.Count).IsEqualTo(2);
        await Assert.That(entrypoints[0]).IsEqualTo(Path.GetFullPath("C:/project/main.asm"));
        await Assert.That(entrypoints[1]).IsEqualTo(Path.GetFullPath("C:/project/test.asm"));
    }

    // ───────────────────────────────────────────────────────────────
    // Task 6.8b: Configured projects override heuristics
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task ConfiguredMode_UsesOnlyConfiguredEntrypoints()
    {
        var manager = CreateManager();

        // Set up files via overlays first
        manager.ApplyConfig(new KohConfigLoadResult.Missing());
        manager.UpdateDocumentText("C:/project/main.asm", "INCLUDE \"lib.inc\"\nnop");
        manager.UpdateDocumentText("C:/project/other.asm", "halt");
        manager.UpdateDocumentText("C:/project/lib.inc", "; lib");

        // Now switch to configured mode with only main.asm
        var configured = new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "Main", Entrypoint = Path.GetFullPath("C:/project/main.asm") },
        ]);
        manager.ApplyConfig(configured);

        await Assert.That(manager.Mode).IsEqualTo(FolderMode.Configured);
        await Assert.That(manager.AllProjects.Count).IsEqualTo(1);

        var project = manager.AllProjects.First();
        await Assert.That(project.Name).IsEqualTo("Main");
        await Assert.That(project.EntrypointPath).IsEqualTo(Path.GetFullPath("C:/project/main.asm"));
    }

    [Test]
    public async Task ConfiguredMode_DoesNotIncludeHeuristicCandidates()
    {
        var manager = CreateManager();
        manager.ApplyConfig(new KohConfigLoadResult.Missing());

        // Create two standalone .asm files (both would be heuristic candidates)
        manager.UpdateDocumentText("C:/project/game.asm", "nop");
        manager.UpdateDocumentText("C:/project/debug.asm", "halt");

        // Switch to configured with only game.asm
        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "Game", Entrypoint = Path.GetFullPath("C:/project/game.asm") },
        ]));

        // Only one project should exist — debug.asm is NOT a project
        await Assert.That(manager.AllProjects.Count).IsEqualTo(1);
        await Assert.That(manager.AllProjects.First().Name).IsEqualTo("Game");
    }

    // ───────────────────────────────────────────────────────────────
    // Task 6.8c: Shared include edits rebuild only affected projects
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task SharedIncludeEdit_RebuildsBothAffectedProjects()
    {
        var manager = CreateManager();

        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "Game", Entrypoint = Path.GetFullPath("C:/project/game.asm") },
            new KohProjectDefinition { Name = "Tools", Entrypoint = Path.GetFullPath("C:/project/tools.asm") },
        ]));

        manager.UpdateDocumentText("C:/project/game.asm", "INCLUDE \"shared.inc\"\nnop");
        manager.UpdateDocumentText("C:/project/tools.asm", "INCLUDE \"shared.inc\"\nhalt");
        manager.UpdateDocumentText("C:/project/shared.inc", "; original");

        // Record versions
        var gameV1 = manager.AllProjects.First(p => p.Name == "Game").GraphVersion;
        var toolsV1 = manager.AllProjects.First(p => p.Name == "Tools").GraphVersion;

        // Edit shared include
        manager.UpdateDocumentText("C:/project/shared.inc", "; updated");

        var gameV2 = manager.AllProjects.First(p => p.Name == "Game").GraphVersion;
        var toolsV2 = manager.AllProjects.First(p => p.Name == "Tools").GraphVersion;

        // Both should have been rebuilt (version incremented)
        await Assert.That(gameV2).IsGreaterThan(gameV1);
        await Assert.That(toolsV2).IsGreaterThan(toolsV1);
    }

    // ───────────────────────────────────────────────────────────────
    // Task 6.8d: Unrelated projects remain isolated
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task UnrelatedProject_NotRebuiltWhenOtherChanges()
    {
        var manager = CreateManager();

        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "Game", Entrypoint = Path.GetFullPath("C:/project/game.asm") },
            new KohProjectDefinition { Name = "Tools", Entrypoint = Path.GetFullPath("C:/project/tools.asm") },
        ]));

        // Game includes gamelib.inc; Tools includes toollib.inc — no shared files
        manager.UpdateDocumentText("C:/project/game.asm", "INCLUDE \"gamelib.inc\"\nnop");
        manager.UpdateDocumentText("C:/project/gamelib.inc", "; game lib");
        manager.UpdateDocumentText("C:/project/tools.asm", "INCLUDE \"toollib.inc\"\nhalt");
        manager.UpdateDocumentText("C:/project/toollib.inc", "; tool lib");

        var toolsVersionBefore = manager.AllProjects.First(p => p.Name == "Tools").GraphVersion;

        // Edit only game's include file
        manager.UpdateDocumentText("C:/project/gamelib.inc", "; game lib updated");

        var toolsVersionAfter = manager.AllProjects.First(p => p.Name == "Tools").GraphVersion;

        // Tools should NOT have been rebuilt
        await Assert.That(toolsVersionAfter).IsEqualTo(toolsVersionBefore);
    }

    // ───────────────────────────────────────────────────────────────
    // Task 6.8e: File outside configured projects gets no ownership
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task FileOutsideConfiguredProjects_HasNoPrimaryOwner()
    {
        var manager = CreateManager();

        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "Game", Entrypoint = Path.GetFullPath("C:/project/game.asm") },
        ]));

        manager.UpdateDocumentText("C:/project/game.asm", "nop");
        manager.UpdateDocumentText("C:/project/orphan.asm", "halt");

        // orphan.asm is not included by any configured project
        var ctx = manager.GetPrimaryProjectContextFor("C:/project/orphan.asm");
        await Assert.That(ctx).IsNull();
    }

    [Test]
    public async Task FileInsideConfiguredProject_HasPrimaryOwner()
    {
        var manager = CreateManager();

        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "Game", Entrypoint = Path.GetFullPath("C:/project/game.asm") },
        ]));

        manager.UpdateDocumentText("C:/project/game.asm", "INCLUDE \"lib.inc\"\nnop");
        manager.UpdateDocumentText("C:/project/lib.inc", "; lib");

        var ctx = manager.GetPrimaryProjectContextFor("C:/project/game.asm");
        await Assert.That(ctx).IsNotNull();
        await Assert.That(ctx!.Name).IsEqualTo("Game");

        var libCtx = manager.GetPrimaryProjectContextFor("C:/project/lib.inc");
        await Assert.That(libCtx).IsNotNull();
        await Assert.That(libCtx!.Name).IsEqualTo("Game");
    }

    // ───────────────────────────────────────────────────────────────
    // Task 6.8f: Invalid configuration blocks heuristic fallback
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task InvalidConfig_NoProjectsBuilt()
    {
        var manager = CreateManager();

        manager.ApplyConfig(new KohConfigLoadResult.Invalid([
            new ConfigValidationError("bad config"),
        ]));

        await Assert.That(manager.Mode).IsEqualTo(FolderMode.InvalidConfiguration);
        await Assert.That(manager.AllProjects.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidConfig_UpdateDocumentDoesNotCreateProjects()
    {
        var manager = CreateManager();

        manager.ApplyConfig(new KohConfigLoadResult.Invalid([
            new ConfigValidationError("bad config"),
        ]));

        manager.UpdateDocumentText("C:/project/main.asm", "nop");

        // Even after updating a document, no projects should be created
        await Assert.That(manager.AllProjects.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidConfig_SurfacesErrors()
    {
        var manager = CreateManager();

        manager.ApplyConfig(new KohConfigLoadResult.Invalid([
            new ConfigValidationError("Missing field: version"),
            new ConfigValidationError("Missing field: projects"),
        ]));

        await Assert.That(manager.ConfigErrors.Count).IsEqualTo(2);
        await Assert.That(manager.ConfigErrors[0].Message).IsEqualTo("Missing field: version");
    }

    // ───────────────────────────────────────────────────────────────
    // Additional: GetProjectContextsFor returns all containing projects
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task GetProjectContextsFor_SharedFile_ReturnsMultipleProjects()
    {
        var manager = CreateManager();

        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "Game", Entrypoint = Path.GetFullPath("C:/project/game.asm") },
            new KohProjectDefinition { Name = "Tools", Entrypoint = Path.GetFullPath("C:/project/tools.asm") },
        ]));

        manager.UpdateDocumentText("C:/project/game.asm", "INCLUDE \"shared.inc\"\nnop");
        manager.UpdateDocumentText("C:/project/tools.asm", "INCLUDE \"shared.inc\"\nhalt");
        manager.UpdateDocumentText("C:/project/shared.inc", "; shared");

        var contexts = manager.GetProjectContextsFor("C:/project/shared.inc");
        await Assert.That(contexts.Count).IsEqualTo(2);
    }

    // ───────────────────────────────────────────────────────────────
    // Additional: Deterministic primary owner selection
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task HeuristicMode_PrimaryOwnerIsDeterministic()
    {
        var manager = CreateManager();
        manager.ApplyConfig(new KohConfigLoadResult.Missing());

        manager.UpdateDocumentText("C:/project/beta.asm", "INCLUDE \"shared.inc\"\nnop");
        manager.UpdateDocumentText("C:/project/alpha.asm", "INCLUDE \"shared.inc\"\nhalt");
        manager.UpdateDocumentText("C:/project/shared.inc", "; shared");

        // alpha.asm sorts first alphabetically, so it should be the primary owner of shared.inc
        var ctx = manager.GetPrimaryProjectContextFor("C:/project/shared.inc");
        await Assert.That(ctx).IsNotNull();
        await Assert.That(ctx!.EntrypointPath).IsEqualTo(Path.GetFullPath("C:/project/alpha.asm"));
    }

    // ───────────────────────────────────────────────────────────────
    // Additional: Remove document
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task RemoveDocument_RemovesFromHeuristicProjects()
    {
        var manager = CreateManager();
        manager.ApplyConfig(new KohConfigLoadResult.Missing());

        manager.UpdateDocumentText("C:/project/main.asm", "nop");
        await Assert.That(manager.AllProjects.Count).IsEqualTo(1);

        manager.RemoveDocument("C:/project/main.asm");
        await Assert.That(manager.AllProjects.Count).IsEqualTo(0);
    }

    // ───────────────────────────────────────────────────────────────
    // Additional: Configured mode — deterministic primary owner
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task ConfiguredMode_PrimaryOwnerIsDeterministic()
    {
        var manager = CreateManager();

        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "Beta", Entrypoint = Path.GetFullPath("C:/project/beta.asm") },
            new KohProjectDefinition { Name = "Alpha", Entrypoint = Path.GetFullPath("C:/project/alpha.asm") },
        ]));

        manager.UpdateDocumentText("C:/project/beta.asm", "INCLUDE \"shared.inc\"\nnop");
        manager.UpdateDocumentText("C:/project/alpha.asm", "INCLUDE \"shared.inc\"\nhalt");
        manager.UpdateDocumentText("C:/project/shared.inc", "; shared");

        // alpha.asm sorts first, so shared.inc primary owner should be alpha
        var ctx = manager.GetPrimaryProjectContextFor("C:/project/shared.inc");
        await Assert.That(ctx).IsNotNull();
        await Assert.That(ctx!.EntrypointPath).IsEqualTo(Path.GetFullPath("C:/project/alpha.asm"));
    }
}
