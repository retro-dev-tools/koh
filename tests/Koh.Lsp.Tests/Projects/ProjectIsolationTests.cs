using Koh.Core;
using Koh.Lsp.Config;
using Koh.Lsp.Discovery;
using Koh.Lsp.Projects;
using Koh.Lsp.Source;

namespace Koh.Lsp.Tests.Projects;

/// <summary>
/// Phase 11: Validate semantics and isolation.
/// Tests cross-project isolation (11.1) and standalone behavior (11.2).
/// </summary>
public class ProjectIsolationTests
{
    private const string FolderPath = "C:/test";

    private static ProjectContextManager CreateManager(VirtualFileResolver? inner = null)
    {
        return new ProjectContextManager(FolderPath, inner ?? new VirtualFileResolver());
    }

    // ───────────────────────────────────────────────────────────────
    // 11.1 — Cross-project isolation
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task TwoConfiguredProjects_DoNotShareSymbols()
    {
        // game.asm defines GameStart, tools.asm defines ToolsMain.
        // Verify game's context doesn't have ToolsMain and vice versa.
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile(
            Path.GetFullPath("C:/test/game.asm"),
            "SECTION \"Game\", ROM0\nGameStart:\n  nop");
        resolver.AddTextFile(
            Path.GetFullPath("C:/test/tools.asm"),
            "SECTION \"Tools\", ROM0\nToolsMain:\n  nop");

        var manager = CreateManager(resolver);
        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "game", Entrypoint = Path.GetFullPath("C:/test/game.asm") },
            new KohProjectDefinition { Name = "tools", Entrypoint = Path.GetFullPath("C:/test/tools.asm") },
        ]));

        manager.UpdateDocumentText("C:/test/game.asm",
            "SECTION \"Game\", ROM0\nGameStart:\n  nop");
        manager.UpdateDocumentText("C:/test/tools.asm",
            "SECTION \"Tools\", ROM0\nToolsMain:\n  nop");

        var gameCtx = manager.AllProjects.First(p => p.Name == "game");
        var toolsCtx = manager.AllProjects.First(p => p.Name == "tools");

        var gameSymbols = gameCtx.Compilation.Emit().Symbols.Select(s => s.Name).ToList();
        var toolsSymbols = toolsCtx.Compilation.Emit().Symbols.Select(s => s.Name).ToList();

        // game should have GameStart but NOT ToolsMain
        await Assert.That(gameSymbols).Contains("GameStart");
        await Assert.That(gameSymbols).DoesNotContain("ToolsMain");

        // tools should have ToolsMain but NOT GameStart
        await Assert.That(toolsSymbols).Contains("ToolsMain");
        await Assert.That(toolsSymbols).DoesNotContain("GameStart");
    }

    [Test]
    public async Task DiagnosticsDontLeakAcrossProjects()
    {
        // game.asm has an error, tools.asm is clean.
        // Verify tools.asm diagnostics don't include game.asm's error.
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile(
            Path.GetFullPath("C:/test/game.asm"),
            "SECTION \"Game\", ROM0\n  ld a, UNDEFINED_SYMBOL");
        resolver.AddTextFile(
            Path.GetFullPath("C:/test/tools.asm"),
            "SECTION \"Tools\", ROM0\nToolsMain:\n  nop");

        var manager = CreateManager(resolver);
        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "game", Entrypoint = Path.GetFullPath("C:/test/game.asm") },
            new KohProjectDefinition { Name = "tools", Entrypoint = Path.GetFullPath("C:/test/tools.asm") },
        ]));

        manager.UpdateDocumentText("C:/test/game.asm",
            "SECTION \"Game\", ROM0\n  ld a, UNDEFINED_SYMBOL");
        manager.UpdateDocumentText("C:/test/tools.asm",
            "SECTION \"Tools\", ROM0\nToolsMain:\n  nop");

        var gameCtx = manager.AllProjects.First(p => p.Name == "game");
        var toolsCtx = manager.AllProjects.First(p => p.Name == "tools");

        var gameDiags = gameCtx.Compilation.Emit().Diagnostics;
        var toolsDiags = toolsCtx.Compilation.Emit().Diagnostics;

        // game should have at least one diagnostic (undefined symbol)
        await Assert.That(gameDiags.Count).IsGreaterThan(0);

        // tools should have zero diagnostics — no leakage from game
        await Assert.That(toolsDiags.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SharedIncludeInMultipleProjects_SeparateContexts()
    {
        // Both game.asm and tools.asm include shared.inc.
        // Verify both project contexts list shared.inc in their reachable files,
        // but they are separate project contexts.
        var resolver = new VirtualFileResolver();
        var sharedPath = Path.GetFullPath("C:/test/shared.inc");
        resolver.AddTextFile(sharedPath, "SharedLabel:\n  nop");
        resolver.AddTextFile(
            Path.GetFullPath("C:/test/game.asm"),
            "INCLUDE \"shared.inc\"\nSECTION \"Game\", ROM0\nnop");
        resolver.AddTextFile(
            Path.GetFullPath("C:/test/tools.asm"),
            "INCLUDE \"shared.inc\"\nSECTION \"Tools\", ROM0\nnop");

        var manager = CreateManager(resolver);
        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "game", Entrypoint = Path.GetFullPath("C:/test/game.asm") },
            new KohProjectDefinition { Name = "tools", Entrypoint = Path.GetFullPath("C:/test/tools.asm") },
        ]));

        manager.UpdateDocumentText("C:/test/game.asm",
            "INCLUDE \"shared.inc\"\nSECTION \"Game\", ROM0\nnop");
        manager.UpdateDocumentText("C:/test/tools.asm",
            "INCLUDE \"shared.inc\"\nSECTION \"Tools\", ROM0\nnop");
        manager.UpdateDocumentText("C:/test/shared.inc",
            "SharedLabel:\n  nop");

        var gameCtx = manager.AllProjects.First(p => p.Name == "game");
        var toolsCtx = manager.AllProjects.First(p => p.Name == "tools");

        // Both projects should list shared.inc in their reachable files
        await Assert.That(gameCtx.ReachableFiles.Contains(sharedPath)).IsTrue();
        await Assert.That(toolsCtx.ReachableFiles.Contains(sharedPath)).IsTrue();

        // They must be distinct project context instances
        await Assert.That(gameCtx.Id).IsNotEqualTo(toolsCtx.Id);
        await Assert.That(gameCtx.EntrypointPath).IsNotEqualTo(toolsCtx.EntrypointPath);

        // Each has its own compilation
        await Assert.That(gameCtx.Compilation).IsNotEqualTo(toolsCtx.Compilation);
    }

    // ───────────────────────────────────────────────────────────────
    // 11.2 — Standalone behavior
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task OrphanAsmGetsStandaloneAnalysis_HeuristicMode()
    {
        // In heuristic mode, open an .asm file that has no includes and isn't included.
        // Verify it gets a project context.
        var manager = CreateManager();
        manager.ApplyConfig(new KohConfigLoadResult.Missing());

        manager.UpdateDocumentText("C:/test/orphan.asm", "SECTION \"Orphan\", ROM0\nnop");

        var ctx = manager.GetPrimaryProjectContextFor("C:/test/orphan.asm");
        await Assert.That(ctx).IsNotNull();
        await Assert.That(ctx!.EntrypointPath).IsEqualTo(Path.GetFullPath("C:/test/orphan.asm"));
    }

    [Test]
    public async Task OpenIncGetsStandaloneAnalysis_HeuristicMode()
    {
        // Open a .inc file directly when no entrypoint includes it.
        // Verify it gets some form of analysis (its own project context with OpenInc score).
        var manager = CreateManager();
        manager.ApplyConfig(new KohConfigLoadResult.Missing());

        manager.UpdateDocumentText("C:/test/standalone.inc", "; standalone include\nnop");

        var ctx = manager.GetPrimaryProjectContextFor("C:/test/standalone.inc");
        await Assert.That(ctx).IsNotNull();

        // The entrypoint should be the .inc file itself (OpenInc candidate)
        await Assert.That(ctx!.EntrypointPath).IsEqualTo(Path.GetFullPath("C:/test/standalone.inc"));
    }

    // ───────────────────────────────────────────────────────────────
    // Regression: included files must be reachable even when
    // the entrypoint was never opened in the editor
    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task IncludedFile_HasPrimaryOwner_WhenEntrypointNotOpened()
    {
        // Simulate: koh.yaml points to src/game.asm as entrypoint.
        // game.asm includes macros.inc then home/main.asm.
        // Only home/main.asm is opened in the editor.
        // home/main.asm should still belong to the game project.
        var resolver = new VirtualFileResolver();
        var gamePath = Path.GetFullPath("C:/test/src/game.asm");
        var macrosPath = Path.GetFullPath("C:/test/src/macros/asm.inc");
        var mainPath = Path.GetFullPath("C:/test/src/home/main.asm");

        resolver.AddTextFile(gamePath,
            "INCLUDE \"macros/asm.inc\"\nINCLUDE \"home/main.asm\"");
        resolver.AddTextFile(macrosPath,
            "MACRO test_macro\n  nop\nENDM");
        resolver.AddTextFile(mainPath,
            "SECTION \"Home\", ROM0\n  test_macro");

        var manager = CreateManager(resolver);
        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "game", Entrypoint = gamePath },
        ]));

        // Only open the included file, NOT the entrypoint
        manager.UpdateDocumentText(mainPath,
            "SECTION \"Home\", ROM0\n  test_macro");

        // main.asm should be owned by the game project
        var ctx = manager.GetPrimaryProjectContextFor(mainPath);
        await Assert.That(ctx).IsNotNull();
        await Assert.That(ctx!.Name).IsEqualTo("game");
    }

    [Test]
    public async Task IncludedFile_NoDiagnosticErrors_WhenMacrosDefinedInEarlierInclude()
    {
        // Reproduce the real scenario: game.asm includes macros, then dialogue_entry.asm.
        // dialogue_entry.asm uses a macro from the earlier include.
        // The compilation should have zero errors.
        var resolver = new VirtualFileResolver();
        var gamePath = Path.GetFullPath("C:/test/src/game.asm");
        var macrosPath = Path.GetFullPath("C:/test/src/macros/asm.inc");
        var dialoguePath = Path.GetFullPath("C:/test/src/home/dialogue_entry.asm");
        var wramPath = Path.GetFullPath("C:/test/src/ram/wram.asm");

        resolver.AddTextFile(gamePath, string.Join("\n",
            "INCLUDE \"ram/wram.asm\"",
            "INCLUDE \"macros/asm.inc\"",
            "SECTION \"Home\", ROM0",
            "INCLUDE \"home/dialogue_entry.asm\""));

        resolver.AddTextFile(wramPath, string.Join("\n",
            "SECTION \"WRAM\", WRAM0",
            "wDialogueState:: db"));

        resolver.AddTextFile(macrosPath, string.Join("\n",
            "MACRO switch_bank",
            "  ld a, \\1",
            "ENDM"));

        resolver.AddTextFile(dialoguePath, string.Join("\n",
            "RunDialogue:",
            "  switch_bank $0d",
            "  ld a, [wDialogueState]",
            "  ret"));

        var manager = CreateManager(resolver);
        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "game", Entrypoint = gamePath },
        ]));

        // Open only the leaf file
        manager.UpdateDocumentText(dialoguePath, string.Join("\n",
            "RunDialogue:",
            "  switch_bank $0d",
            "  ld a, [wDialogueState]",
            "  ret"));

        // Get the project context for dialogue_entry.asm
        var ctx = manager.GetPrimaryProjectContextFor(dialoguePath);
        await Assert.That(ctx).IsNotNull();

        // The compilation should succeed with no errors
        var diags = ctx!.Compilation.Emit().Diagnostics;
        var errors = diags.Where(d => d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Error).ToList();
        await Assert.That(errors.Count).IsEqualTo(0);
    }

    // ───────────────────────────────────────────────────────────────

    [Test]
    public async Task FileOutsideConfiguredProjects_NotAutoAssigned()
    {
        // In configured mode with one project, open a file that's NOT part of that project.
        // Verify it has no primary project context (not auto-assigned to heuristic).
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile(
            Path.GetFullPath("C:/test/game.asm"),
            "SECTION \"Game\", ROM0\nnop");

        var manager = CreateManager(resolver);
        manager.ApplyConfig(new KohConfigLoadResult.Configured([
            new KohProjectDefinition { Name = "game", Entrypoint = Path.GetFullPath("C:/test/game.asm") },
        ]));

        manager.UpdateDocumentText("C:/test/game.asm", "SECTION \"Game\", ROM0\nnop");
        manager.UpdateDocumentText("C:/test/unrelated.asm", "SECTION \"Other\", ROM0\nhalt");

        // unrelated.asm is not included by any configured project
        var ctx = manager.GetPrimaryProjectContextFor("C:/test/unrelated.asm");
        await Assert.That(ctx).IsNull();

        // Also verify it didn't create a heuristic project for it
        var contexts = manager.GetProjectContextsFor("C:/test/unrelated.asm");
        await Assert.That(contexts.Count).IsEqualTo(0);
    }
}
