# Plan 2 — LSP Symbol Features

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement all LSP symbol features — rename, semantic tokens, inlay hints, signature help — on top of the compiler identity foundation from Plan 1.

**Dependency:** This plan depends on Plan 1 (Compiler Identity Foundation) being complete. The ownership model, `SymbolId`, `ResolveSymbol`, and `SymbolResolutionContext` must all be in place before starting.

**Dependency:** Task 5 (Signature Help) depends on Plan 3 Task 1 (Macro Arity Metadata) being complete.

**Non-goals:** This plan does NOT include compiler ownership redesign, linker diagnostics, parallel parsing, AOT, or incremental parsing. It does NOT duplicate the Design Constraints or Export Visibility Rules — see Plan 1 for those.

**Spec:** `docs/superpowers/specs/2026-03-25-koh-assembler-design.md`
**Original plan:** `docs/superpowers/plans/2026-03-25-koh-implementation-plan.md`
**Parent index:** `docs/superpowers/plans/2026-04-02-koh-completion-plan.md`

**Exit criteria:** All LSP symbol features (rename, semantic tokens, inlay hints, signature help) pass automated tests. Manual verification in VS Code confirms end-to-end behavior. Exported symbols rename across files. Owner-local symbols rename within their owner only.

---

## Phase Overview

| Phase | Task | Milestone | You can now... |
|-------|------|-----------|----------------|
| A | 1 | LSP Identity Adaptation | SymbolFinder uses SymbolId-based matching for rename and references |
| B | 2 | Rename | Rename handlers, validation, and single-owner rename tests |
| B | 3 | Semantic Tokens | See syntax-tree-driven highlighting via explicit classification tables |
| B | 4 | Inlay Hints | See resolved constant/address values inline for labels and EQU constants |
| B | 5 | Signature Help | See observed macro argument count while typing macro calls |
| B | 6 | Manual Verification | All LSP features verified end-to-end in VS Code (manual) |

---

## Phase A — LSP Infrastructure

## Task 1: LSP Adaptation to Ownership-Aware Symbols

**Milestone:** `SymbolFinder`, rename preparation, and reference search use `SymbolId` for identity matching instead of qualified name strings. Cross-file behavior is correct for both owner-local and exported symbols.

**Depends on:** Plan 1 (all tasks complete — `SymbolId`, `ResolveSymbol`, owner-scoped `SymbolTable`).

**Files:**
- Create: `src/Koh.Lsp/SymbolFinder.cs`
- Create: `tests/Koh.Lsp.Tests/TestHelpers.cs`
- Modify: `src/Koh.Lsp/KohLanguageServer.cs` (if needed)
- Test: `tests/Koh.Lsp.Tests/CrossFileRenameTests.cs`

- [ ] **Step 1: Create `SymbolFinder` with `SymbolId`-based matching**

```csharp
// src/Koh.Lsp/SymbolFinder.cs
namespace Koh.Lsp;

/// <summary>
/// Finds symbol occurrences across open files using SymbolId-based identity matching.
/// </summary>
public class SymbolFinder
{
    // ResolvedSymbol record stores the Symbol (which carries SymbolId),
    // not a redundant qualified name string.
    public record ResolvedSymbol(Symbol Symbol, SyntaxToken Token, string FilePath);

    /// <summary>
    /// Resolve the symbol at a given position in a document.
    /// Returns null if no symbol-bearing ancestor or resolution fails.
    /// </summary>
    public ResolvedSymbol? ResolveAt(SemanticModel model, SyntaxTree tree, int position, string filePath);

    /// <summary>
    /// Find all occurrences of a symbol across open files.
    /// For exported symbols: searches all open files.
    /// For owner-local symbols: searches only the owning translation unit's files.
    /// Matches by SymbolId, not by name string.
    /// </summary>
    public IReadOnlyList<ResolvedSymbol> FindAllOccurrences(
        ResolvedSymbol target,
        Workspace workspace);

    /// <summary>
    /// Validate a proposed new name for a symbol.
    /// For owner-local symbols: checks for collisions within owner scope.
    /// For exported symbols: also checks the project-visible exported namespace.
    /// Returns null if valid, or an error message string if invalid.
    /// </summary>
    public string? ValidateNewName(
        ResolvedSymbol target,
        string newName,
        Workspace workspace);
}
```

- [ ] **Step 2: Implement `FindAllOccurrences` with scope-aware search**

```csharp
// For exported symbols: SymbolId = (null, "main") — search ALL open files
// For owner-local symbols: SymbolId = ("a.asm", "count") — search only files
//   belonging to that owner (root file + its includes)

// Match by SymbolId:
// resolved.Symbol.SymbolId == target.Symbol.SymbolId
```

- [ ] **Step 3: Implement `ValidateNewName` with owner-scoped collision checks**

```csharp
// Owner-local collision: check within owner scope
var existing = model.ResolveSymbol(newName, position);
if (existing != null && existing.SymbolId != target.Symbol.SymbolId)
    return $"Symbol '{newName}' already exists";

// For exported symbols, also check the project-visible exported namespace
```

- [ ] **Step 4: Create TestHelpers**

```csharp
// tests/Koh.Lsp.Tests/TestHelpers.cs
using Koh.Lsp;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;

namespace Koh.Lsp.Tests;

internal static class TestHelpers
{
    public static KohLanguageServer CreateServer(Workspace workspace)
        => new(rpc: null!, workspace);

    public static JToken RenameParams(string uri, int line, int character, string newName)
        => JToken.FromObject(new RenameParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(uri) },
            Position = new Position(line, character),
            NewName = newName,
        });

    public static JToken PositionParams(string uri, int line, int character)
        => JToken.FromObject(new TextDocumentPositionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(uri) },
            Position = new Position(line, character),
        });
}
```

- [ ] **Step 5: Write cross-file rename tests**

```csharp
// tests/Koh.Lsp.Tests/CrossFileRenameTests.cs
namespace Koh.Lsp.Tests;

public class CrossFileRenameTests
{
    [Test]
    public async Task Rename_CrossFile_PrivateSymbolsIndependent()
    {
        // Non-exported same-name symbols in different files do not cross-rename
        var ws = new Workspace();
        ws.OpenDocument("file:///a.asm",
            "SECTION \"A\", ROM0\ncount:\n    nop\n    jp count\n");
        ws.OpenDocument("file:///b.asm",
            "SECTION \"B\", ROM0\ncount EQU 42\n    ld a, count\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///a.asm", line: 1, character: 0, newName: "counter"));

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Changes!.ContainsKey("file:///a.asm")).IsTrue();
        await Assert.That(result!.Changes!.ContainsKey("file:///b.asm")).IsFalse();
    }

    [Test]
    public async Task Rename_CrossFile_ExportedSymbolAffectsAll()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///a.asm",
            "SECTION \"A\", ROM0\nmain:\n    EXPORT main\n    nop\n");
        ws.OpenDocument("file:///b.asm",
            "SECTION \"B\", ROM0\n    jp main\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///a.asm", line: 1, character: 0, newName: "entry"));

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Changes!.ContainsKey("file:///a.asm")).IsTrue();
        await Assert.That(result!.Changes!.ContainsKey("file:///b.asm")).IsTrue();
    }

    [Test]
    public async Task Rename_ExportedSymbol_TouchesExportDirective()
    {
        // Renaming "main" should also update the name in "EXPORT main"
        var ws = new Workspace();
        ws.OpenDocument("file:///a.asm",
            "SECTION \"A\", ROM0\nmain:\n    EXPORT main\n    nop\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///a.asm", line: 1, character: 0, newName: "entry"));

        await Assert.That(result).IsNotNull();
        var edits = result!.Changes!["file:///a.asm"];
        // declaration + EXPORT directive reference + any other refs
        await Assert.That(edits.Length).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task Rename_IncludeBasedSameOwner()
    {
        // Symbol defined in include, referenced in root — same owner, rename works
        var ws = new Workspace();
        ws.OpenDocument("file:///main.asm",
            "SECTION \"A\", ROM0\nINCLUDE \"defs.inc\"\n    ld a, MY_CONST\n");
        ws.OpenDocument("file:///defs.inc",
            "MY_CONST EQU 42\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///main.asm", line: 2, character: 10, newName: "THE_CONST"));

        await Assert.That(result).IsNotNull();
        // Should touch both files since they are the same owner
        await Assert.That(result!.Changes!.ContainsKey("file:///main.asm")).IsTrue();
        await Assert.That(result!.Changes!.ContainsKey("file:///defs.inc")).IsTrue();
    }

    [Test]
    public async Task Rename_OwnerLocalAndExportedInteraction()
    {
        // b.asm has owner-local "count" that shadows a.asm's exported "count"
        // Renaming b.asm's "count" should not affect a.asm
        var ws = new Workspace();
        ws.OpenDocument("file:///a.asm",
            "SECTION \"A\", ROM0\ncount:\n    EXPORT count\n");
        ws.OpenDocument("file:///b.asm",
            "SECTION \"B\", ROM0\ncount EQU 99\n    ld a, count\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///b.asm", line: 1, character: 0, newName: "total"));

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Changes!.ContainsKey("file:///b.asm")).IsTrue();
        await Assert.That(result!.Changes!.ContainsKey("file:///a.asm")).IsFalse();
    }
}
```

- [ ] **Step 6: Run tests, verify pass**

```bash
cd /c/projekty/koh && dotnet test
```

- [ ] **Step 7: Commit**

```bash
git add src/Koh.Lsp/SymbolFinder.cs tests/Koh.Lsp.Tests/TestHelpers.cs tests/Koh.Lsp.Tests/CrossFileRenameTests.cs
git commit -m "feat(lsp): add SymbolFinder with SymbolId-based identity matching"
```

---

## Phase B — User-Facing Handlers

## Task 2: Rename

**Milestone:** `textDocument/rename` renames a label, EQU/EQUS constant, or macro by `SymbolId`-based identity. For exported symbols, rename affects all files and updates the `EXPORT` directive. For owner-local symbols, rename affects only the owning translation unit's files. `textDocument/prepareRename` validates the target.

**Scope:** This task implements only the `prepareRename` and `rename` handlers, name validation, and rename-specific tests. It uses `SymbolFinder` from Task 1 (does not recreate it). Cross-file tests belong in Task 1; this task tests single-owner rename scenarios and name validation.

**Files:**
- Modify: `src/Koh.Lsp/KohLanguageServer.cs` — add `PrepareRename` and `Rename` handlers
- Test: `tests/Koh.Lsp.Tests/RenameTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using Koh.Lsp;

namespace Koh.Lsp.Tests;

public class RenameTests
{
    // --- Basic cases ---

    [Test]
    public async Task Rename_GlobalLabel_RenamesAllReferences()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nmain:\n    jp main\n    call main\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 1, character: 0, newName: "entry"));

        await Assert.That(result).IsNotNull();
        var edits = result!.Changes!["file:///test.asm"];
        await Assert.That(edits.Length).IsEqualTo(3);
    }

    [Test]
    public async Task Rename_LocalLabel_OnlyRenamesInScope()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "main:\n.loop:\n    jr .loop\n" +
            "other:\n.loop:\n    jr .loop\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 2, character: 0, newName: ".spin"));

        await Assert.That(result).IsNotNull();
        var edits = result!.Changes!["file:///test.asm"];
        await Assert.That(edits.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Rename_EquConstant_RenamesDeclarationAndReferences()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nFOO EQU 42\n    ld a, FOO\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 1, character: 0, newName: "BAR"));

        await Assert.That(result).IsNotNull();
        var edits = result!.Changes!["file:///test.asm"];
        await Assert.That(edits.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Rename_EqusConstant_Works()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nGREET EQUS \"hello\"\n    PRINTLN GREET\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 1, character: 0, newName: "MSG"));

        await Assert.That(result).IsNotNull();
        var edits = result!.Changes!["file:///test.asm"];
        await Assert.That(edits.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Rename_MacroName_RenamesDefinitionAndCallSites()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "my_macro: MACRO\n    nop\nENDM\n" +
            "    my_macro\n    my_macro\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 4, character: 4, newName: "do_thing"));

        await Assert.That(result).IsNotNull();
        var edits = result!.Changes!["file:///test.asm"];
        // 3 occurrences: definition label + 2 call sites
        await Assert.That(edits.Length).IsEqualTo(3);
    }

    // --- From reference ---

    [Test]
    public async Task Rename_FromReference_RenamesDeclarationToo()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nmain:\n    jp main\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 2, character: 7, newName: "entry"));

        await Assert.That(result).IsNotNull();
        var edits = result!.Changes!["file:///test.asm"];
        await Assert.That(edits.Length).IsEqualTo(2);
    }

    // --- Exported symbol updates EXPORT directive ---

    [Test]
    public async Task Rename_ExportedSymbol_UpdatesExportDirective()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nmain:\n    EXPORT main\n    jp main\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 1, character: 0, newName: "entry"));

        await Assert.That(result).IsNotNull();
        var edits = result!.Changes!["file:///test.asm"];
        // declaration + EXPORT ref + jp ref = 3
        await Assert.That(edits.Length).IsEqualTo(3);
    }

    // --- Rejection: keywords and registers ---

    [Test]
    public async Task PrepareRename_Keyword_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm", "SECTION \"Main\", ROM0\nnop\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.PrepareRename(TestHelpers.PositionParams(
            "file:///test.asm", line: 1, character: 0));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task PrepareRename_Register_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm", "SECTION \"Main\", ROM0\nld a, b\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.PrepareRename(TestHelpers.PositionParams(
            "file:///test.asm", line: 1, character: 3));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Rename_UnresolvedSymbol_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n    jp nowhere\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 1, character: 7, newName: "somewhere"));

        await Assert.That(result).IsNull();
    }

    // --- Name validation ---

    [Test]
    public async Task Rename_ToReservedKeyword_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nmain:\n    jp main\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 1, character: 0, newName: "nop"));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Rename_ToInvalidIdentifier_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nmain:\n    jp main\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 1, character: 0, newName: "123invalid"));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Rename_LocalToGlobalForm_ReturnsNull()
    {
        // Renaming a local label (.loop) to a global form (no dot prefix) is invalid
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nmain:\n.loop:\n    jr .loop\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 2, character: 0, newName: "global_name"));

        await Assert.That(result).IsNull();
    }

    // --- Collision tests ---

    [Test]
    public async Task Rename_LabelToExistingLabel_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nmain:\n    nop\nother:\n    jp main\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 1, character: 0, newName: "other"));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Rename_LabelToExistingMacroName_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "my_macro: MACRO\n    nop\nENDM\n" +
            "main:\n    nop\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 4, character: 0, newName: "my_macro"));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Rename_MacroToExistingLabel_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "my_macro: MACRO\n    nop\nENDM\n" +
            "main:\n    my_macro\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 5, character: 4, newName: "main"));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Rename_MacroToExistingMacro_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "mac_a: MACRO\n    nop\nENDM\n" +
            "mac_b: MACRO\n    halt\nENDM\n" +
            "    mac_a\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 7, character: 4, newName: "mac_b"));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Rename_LocalToExistingLocalInSameScope_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "main:\n.loop:\n.done:\n    jr .loop\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 2, character: 0, newName: ".done"));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Rename_LocalToSameNameInDifferentScope_Allowed()
    {
        // .loop exists under funcA and funcB — renaming funcA's .loop to .spin
        // should succeed even though funcB also has a .loop (different scope)
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "funcA:\n.loop:\n    jr .loop\n" +
            "funcB:\n.loop:\n    jr .loop\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.Rename(TestHelpers.RenameParams(
            "file:///test.asm", line: 2, character: 0, newName: ".spin"));

        await Assert.That(result).IsNotNull();
        var edits = result!.Changes!["file:///test.asm"];
        await Assert.That(edits.Length).IsEqualTo(2); // funcA's .loop decl + ref only
    }
}
```

- [ ] **Step 2: Implement `PrepareRename` handler**

```csharp
// In KohLanguageServer.cs
// 1. Find token at position
// 2. Ancestor context gate: verify symbol-bearing ancestor
// 3. Resolve via SymbolFinder.ResolveAt
// 4. If null or unresolved, return null
// 5. Return the token's range as the renameable range
```

- [ ] **Step 3: Implement `Rename` handler**

```csharp
// In KohLanguageServer.cs
// 1. Validate new name:
//    - IsValidIdentifier check (matches [A-Za-z_][A-Za-z0-9_]* or .[A-Za-z_][A-Za-z0-9_]*)
//    - Lexer.IsKeyword rejection
//    - Register name rejection
//    - Local-to-global form mismatch rejection
// 2. Resolve symbol at position via SymbolFinder.ResolveAt
// 3. If null, return null
// 4. Check post-resolution kind filter (Label/Constant/StringConstant/Macro)
// 5. Validate new name via SymbolFinder.ValidateNewName (collision check)
// 6. Find all occurrences via SymbolFinder.FindAllOccurrences
// 7. Build WorkspaceEdit with text edits grouped by file URI
```

- [ ] **Step 4: Run tests, verify pass**

```bash
cd /c/projekty/koh && dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Lsp/KohLanguageServer.cs tests/Koh.Lsp.Tests/RenameTests.cs
git commit -m "feat(lsp): implement textDocument/rename and prepareRename handlers"
```

---

## Task 3: Semantic Tokens

**Milestone:** `textDocument/semanticTokens/full` using explicit `HashSet<SyntaxKind>` classification.

**Design decisions (explicit):**
- Condition flags (`Z`, `NZ`, `NC`) are classified as `Keyword` together with instructions. This is a deliberate UX choice — they appear in the same syntactic position as conditions and coloring them differently from the instruction would look noisy.
- All user-defined identifiers (labels, constants, macro names when referenced) are classified as `Function`. This is intentionally coarse for v1. Differentiating label-references from constant-references would require semantic model queries per token, which is expensive. Documented as a known simplification.
- A guard test ensures new `SyntaxKind` values are not silently unclassified.

**Files:**
- Modify: `src/Koh.Lsp/KohLanguageServer.cs`
- Create: `src/Koh.Lsp/SemanticTokenEncoder.cs`
- Test: `tests/Koh.Lsp.Tests/SemanticTokenTests.cs`
- Test: `tests/Koh.Core.Tests/Syntax/SyntaxKindCoverageTests.cs`

- [ ] **Step 1: Write failing tests — assert decoded position and type, not length scanning**

```csharp
using Koh.Lsp;

namespace Koh.Lsp.Tests;

public class SemanticTokenTests
{
    // Each token = 5 uints: deltaLine, deltaStart, length, tokenType, tokenModifiers.
    // Tests decode the full array and check specific tokens by (line, col, type).

    [Test]
    public async Task SemanticTokens_Instruction_ClassifiedAsKeyword()
    {
        var tokens = GetDecodedTokens("nop\n");

        await Assert.That(tokens.Count).IsGreaterThan(0);
        var nop = tokens.First(t => t.Line == 0 && t.Col == 0);
        await Assert.That(nop.Length).IsEqualTo(3);
        await Assert.That(nop.Type).IsEqualTo(SemanticTokenEncoder.Keyword);
    }

    [Test]
    public async Task SemanticTokens_Register_ClassifiedAsVariable()
    {
        var tokens = GetDecodedTokens("ld a, b\n");

        var regA = tokens.First(t => t.Line == 0 && t.Length == 1 && t.Col == 3);
        await Assert.That(regA.Type).IsEqualTo(SemanticTokenEncoder.Variable);

        var regB = tokens.First(t => t.Line == 0 && t.Length == 1 && t.Col == 6);
        await Assert.That(regB.Type).IsEqualTo(SemanticTokenEncoder.Variable);
    }

    [Test]
    public async Task SemanticTokens_LabelDecl_ClassifiedAsFunction()
    {
        var tokens = GetDecodedTokens("main:\n");

        var label = tokens.First(t => t.Line == 0 && t.Col == 0);
        await Assert.That(label.Type).IsEqualTo(SemanticTokenEncoder.Function);
    }

    [Test]
    public async Task SemanticTokens_Comment_ClassifiedAsComment()
    {
        var tokens = GetDecodedTokens("; comment\nnop\n");

        var comment = tokens.First(t => t.Line == 0 && t.Col == 0);
        await Assert.That(comment.Type).IsEqualTo(SemanticTokenEncoder.Comment);
    }

    [Test]
    public async Task SemanticTokens_Directive_ClassifiedAsMacro()
    {
        var tokens = GetDecodedTokens("SECTION \"Main\", ROM0\n");

        var section = tokens.First(t => t.Line == 0 && t.Col == 0);
        await Assert.That(section.Type).IsEqualTo(SemanticTokenEncoder.Macro);
    }

    [Test]
    public async Task SemanticTokens_Number_ClassifiedAsNumber()
    {
        var tokens = GetDecodedTokens("ld a, $42\n");

        var num = tokens.First(t => t.Line == 0 && t.Col == 6);
        await Assert.That(num.Type).IsEqualTo(SemanticTokenEncoder.Number);
    }

    [Test]
    public async Task SemanticTokens_String_ClassifiedAsString()
    {
        var tokens = GetDecodedTokens("SECTION \"Hello\", ROM0\n");

        var str = tokens.First(t => t.Line == 0 && t.Col == 8);
        await Assert.That(str.Type).IsEqualTo(SemanticTokenEncoder.String);
    }

    [Test]
    public async Task SemanticTokens_LocalLabel_ClassifiedAsParameter()
    {
        var tokens = GetDecodedTokens("main:\n.loop:\n");

        var local = tokens.First(t => t.Line == 1 && t.Col == 0);
        await Assert.That(local.Type).IsEqualTo(SemanticTokenEncoder.Parameter);
    }

    [Test]
    public async Task SemanticTokens_Operator_ClassifiedAsOperator()
    {
        var tokens = GetDecodedTokens("ld a, 1 + 2\n");

        var plus = tokens.First(t => t.Line == 0 && t.Col == 8 && t.Length == 1);
        await Assert.That(plus.Type).IsEqualTo(SemanticTokenEncoder.Operator);
    }

    [Test]
    public async Task SemanticTokens_ConditionFlag_ClassifiedAsKeyword()
    {
        // Condition flags (z, nz, nc) are deliberately classified as Keyword
        var tokens = GetDecodedTokens("jr nz, .loop\n");

        var nz = tokens.First(t => t.Line == 0 && t.Col == 3);
        await Assert.That(nz.Type).IsEqualTo(SemanticTokenEncoder.Keyword);
    }

    /// <summary>Decode the raw uint[] into (line, col, length, type) tuples.</summary>
    private static List<DecodedToken> GetDecodedTokens(string source)
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm", source);
        var server = TestHelpers.CreateServer(ws);
        var result = server.SemanticTokensFull(
            TestHelpers.SemanticTokensParams("file:///test.asm"));

        var tokens = new List<DecodedToken>();
        if (result?.Data == null) return tokens;

        int line = 0, col = 0;
        for (int i = 0; i + 4 < result.Data.Length; i += 5)
        {
            int deltaLine = (int)result.Data[i];
            int deltaStart = (int)result.Data[i + 1];
            if (deltaLine > 0)
            {
                line += deltaLine;
                col = deltaStart; // absolute column on new line
            }
            else
            {
                col += deltaStart; // relative to previous token on same line
            }
            tokens.Add(new DecodedToken(line, col,
                (int)result.Data[i + 2], (int)result.Data[i + 3]));
        }
        return tokens;
    }

    private sealed record DecodedToken(int Line, int Col, int Length, int Type);
}
```

- [ ] **Step 2: Write SyntaxKind coverage guard test**

This test ensures every non-special `SyntaxKind` is accounted for in at least one classification set. New enum members that are not added to any set will cause this test to fail.

```csharp
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class SyntaxKindCoverageTests
{
    [Test]
    public async Task AllTokenSyntaxKinds_AreClassifiedOrExplicitlySkipped()
    {
        // Use the encoder's own published sets — no local skip list
        var classified = Koh.Lsp.SemanticTokenEncoder.AllClassifiedKinds;
        var skipped = Koh.Lsp.SemanticTokenEncoder.IntentionallyUnclassifiedKinds;

        // Node and trivia kinds are structural, not token-producing — exclude them
        var structuralKinds = new HashSet<SyntaxKind>
        {
            SyntaxKind.CompilationUnit, SyntaxKind.InstructionStatement,
            SyntaxKind.LabelDeclaration, SyntaxKind.DirectiveStatement,
            SyntaxKind.SectionDirective, SyntaxKind.DataDirective,
            SyntaxKind.SymbolDirective, SyntaxKind.ConditionalDirective,
            SyntaxKind.MacroDefinition, SyntaxKind.MacroCall,
            SyntaxKind.RepeatDirective, SyntaxKind.IncludeDirective,
            SyntaxKind.RegisterOperand, SyntaxKind.ImmediateOperand,
            SyntaxKind.IndirectOperand, SyntaxKind.ConditionOperand,
            SyntaxKind.LabelOperand,
            SyntaxKind.LiteralExpression, SyntaxKind.NameExpression,
            SyntaxKind.BinaryExpression, SyntaxKind.UnaryExpression,
            SyntaxKind.ParenthesizedExpression, SyntaxKind.FunctionCallExpression,
            SyntaxKind.WhitespaceTrivia, SyntaxKind.LineCommentTrivia,
            SyntaxKind.BlockCommentTrivia, SyntaxKind.NewlineTrivia,
            SyntaxKind.SkippedTokensTrivia,
        };

        var unaccounted = Enum.GetValues<SyntaxKind>()
            .Where(k => !structuralKinds.Contains(k)
                     && !classified.Contains(k)
                     && !skipped.Contains(k))
            .ToList();

        await Assert.That(unaccounted)
            .IsEmpty()
            .Because($"Unclassified token kinds: {string.Join(", ", unaccounted)}");
    }
}
```

This requires `SemanticTokenEncoder` to expose `AllClassifiedKinds` (union of classified sets) and `IntentionallyUnclassifiedKinds` (punctuation, EOF, etc.).

Note: the coverage test only checks token-producing `SyntaxKind` values, not node kinds or trivia kinds. The `IntentionallyUnclassifiedKinds` set explicitly documents which token kinds are deliberately left uncolored, preventing fake coverage pressure.

- [ ] **Step 3: Implement SemanticTokenEncoder**

Same as v2 but with explicit `HashSet<SyntaxKind>` sets (no range checks). Add:

```csharp
/// <summary>Union of all classified SyntaxKinds.</summary>
public static readonly HashSet<SyntaxKind> AllClassifiedKinds =
    new(InstructionKeywords
        .Concat(RegisterKeywords)
        .Concat(DirectiveKeywords)
        .Concat(NumericLiterals)
        .Concat(OperatorTokens)
        .Concat(BuiltInFunctions)
        .Append(SyntaxKind.StringLiteral)
        .Append(SyntaxKind.IdentifierToken)
        .Append(SyntaxKind.LocalLabelToken)
        .Append(SyntaxKind.MacroParamToken));

/// <summary>Token kinds deliberately left unclassified.</summary>
public static readonly HashSet<SyntaxKind> IntentionallyUnclassifiedKinds = new()
{
    SyntaxKind.None, SyntaxKind.EndOfFileToken, SyntaxKind.BadToken,
    SyntaxKind.MissingToken,
    SyntaxKind.CommaToken, SyntaxKind.OpenParenToken, SyntaxKind.CloseParenToken,
    SyntaxKind.OpenBracketToken, SyntaxKind.CloseBracketToken,
    SyntaxKind.ColonToken, SyntaxKind.DoubleColonToken, SyntaxKind.DotToken,
    SyntaxKind.HashToken,
    SyntaxKind.CurrentAddressToken, SyntaxKind.AtToken,
    SyntaxKind.AnonLabelForwardToken, SyntaxKind.AnonLabelBackwardToken,
};
```

Note: `FixedPointLiteral` is already in `NumericLiterals` — not appended separately.

Macro names in `MacroCall` nodes are classified as `Function` for syntax coloring. After Task 2 of Plan 1, macros ARE in `SymbolTable` and are renameable. The `Function` classification is a syntactic shortcut — the encoder does not query the semantic model per-token for performance reasons. This means macro names and label references get the same color, which is acceptable for v1.

- [ ] **Step 4: Register capability and implement handler**

- [ ] **Step 5: Run tests, verify pass**

- [ ] **Step 6: Commit**

---

## Task 4: Inlay Hints

**Milestone:** `textDocument/inlayHint` shows numeric values inline for label addresses (`= $0150`) and EQU constants (`= $00A0 (160)`) at reference sites. EQUS (string constants) and macros produce no hints — they have no meaningful numeric value.

**Key design:**
- Uses `FindAncestor` as a **context gate** — verifies the token is in a symbol-bearing context (reference ancestor exists) before resolving. The ancestor node is not used for resolution itself.
- Resolution via `ResolveSymbol(token.Text, position)` — position-aware, correct for local labels.
- Kind filter after resolution: only `SymbolKind.Label` and `SymbolKind.Constant` produce hints. `StringConstant` and `Macro` are excluded.
- Deduplication by `(uri, span.Start)` to prevent duplicate hints from overlapping tree walks.
- Hints shown only on references, not declarations.

**Files:**
- Modify: `src/Koh.Lsp/KohLanguageServer.cs`
- Test: `tests/Koh.Lsp.Tests/InlayHintTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using Koh.Lsp;

namespace Koh.Lsp.Tests;

public class InlayHintTests
{
    [Test]
    public async Task InlayHint_EquConstant_ShowsValue()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nSCREEN_W EQU 160\n    ld a, SCREEN_W\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 3));

        await Assert.That(hints).IsNotNull();
        await Assert.That(hints!.Length).IsEqualTo(1); // only the reference, not declaration
        await Assert.That(hints[0].Label!.First!.Value).Contains("$00A0");
    }

    [Test]
    public async Task InlayHint_NoHintForConstantDeclaration()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nMY_CONST EQU 42\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 2));

        await Assert.That(hints == null || hints.Length == 0).IsTrue();
    }

    [Test]
    public async Task InlayHint_NoHintForLabelDeclaration()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nmain:\n    nop\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 3));

        // "main:" is a declaration — no hint
        await Assert.That(hints == null || hints.Length == 0).IsTrue();
    }

    [Test]
    public async Task InlayHint_NoHintForUnresolved()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n    ld a, UNDEFINED_THING\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 2));

        await Assert.That(hints == null || hints.Length == 0).IsTrue();
    }

    [Test]
    public async Task InlayHint_NoHintForKeywords()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm", "SECTION \"Main\", ROM0\nnop\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 2));

        await Assert.That(hints == null || hints.Length == 0).IsTrue();
    }

    [Test]
    public async Task InlayHint_NoHintForEqusReference()
    {
        // EQUS (StringConstant) has no meaningful numeric value — no hint
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nGREET EQUS \"hello\"\n    PRINTLN GREET\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 3));

        await Assert.That(hints == null || hints.Length == 0).IsTrue();
    }

    [Test]
    public async Task InlayHint_NoHintForMacroDeclarationLabel()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "my_macro: MACRO\n    nop\nENDM\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 4));

        await Assert.That(hints == null || hints.Length == 0).IsTrue();
    }

    [Test]
    public async Task InlayHint_NoHintForMacroCallName()
    {
        // Macro symbols have value 0 — must not show misleading "= $0000" hint
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "my_macro: MACRO\n    nop\nENDM\n" +
            "    my_macro\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 5));

        await Assert.That(hints == null || hints.Length == 0).IsTrue();
    }

    [Test]
    public async Task InlayHint_LabelReference_ShowsAddress()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nmain:\n    nop\nother:\n    jp main\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 5));

        await Assert.That(hints).IsNotNull();
        await Assert.That(hints!.Length).IsGreaterThan(0);
        await Assert.That(hints[0].Label!.First!.Value).Contains("$");
    }

    [Test]
    public async Task InlayHint_ConstantShowsDecimalAndHex()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nVAL EQU 255\n    ld a, VAL\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 3));

        await Assert.That(hints).IsNotNull();
        await Assert.That(hints!.Length).IsEqualTo(1);
        var label = hints[0].Label!.First!.Value;
        await Assert.That(label).Contains("$00FF");
        await Assert.That(label).Contains("255");
    }

    [Test]
    public async Task InlayHint_LocalLabel_CorrectScope()
    {
        // .done under funcA has a different address than .done under funcB
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "funcA:\n.done:\n    jr .done\n" +
            "funcB:\n.done:\n    jr .done\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 7));

        // Should have hints for the "jr .done" references (not declarations)
        await Assert.That(hints).IsNotNull();
        await Assert.That(hints!.Length).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task InlayHint_NoDuplicates()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nVAL EQU 10\n    ld a, VAL\n    ld b, VAL\n");

        var server = TestHelpers.CreateServer(ws);
        var hints = server.InlayHint(TestHelpers.InlayHintParams(
            "file:///test.asm", startLine: 0, endLine: 4));

        await Assert.That(hints).IsNotNull();
        // Two references = two hints, not more
        await Assert.That(hints!.Length).IsEqualTo(2);
        // No duplicate positions
        var positions = hints.Select(h => h.Position.Line * 10000 + h.Position.Character).ToList();
        await Assert.That(positions.Distinct().Count()).IsEqualTo(positions.Count);
    }
}
```

- [ ] **Step 2: Implement handler using ResolveSymbol + FindAncestor**

Core logic for each identifier token in the requested range:

```csharp
// Skip declarations — hints on references only
if (SymbolFinder.FindAncestor(token, SymbolFinder.DeclaredSymbolKinds) != null)
    continue;

// Must have a bindable reference ancestor
if (SymbolFinder.FindAncestor(token, SymbolFinder.ReferencedSymbolKinds) == null)
    continue;

// Resolve via position-aware method
var sym = model.ResolveSymbol(token.Text, token.Span.Start);
if (sym == null || sym.State != SymbolState.Defined) continue;

// Kind filter: only Label and Constant produce numeric hints.
// StringConstant (EQUS) and Macro have no meaningful numeric value.
if (sym.Kind is not SymbolKind.Label and not SymbolKind.Constant) continue;

// Dedup
var key = (uri, token.Span.Start);
if (!seen.Add(key)) continue;

// Emit hint
string label = sym.Kind == SymbolKind.Label
    ? $"= ${sym.Value:X4}"
    : $"= ${sym.Value:X4} ({sym.Value})";
```

- [ ] **Step 3: Run tests, verify pass**

```bash
cd /c/projekty/koh && dotnet test tests/Koh.Lsp.Tests
```

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Lsp/KohLanguageServer.cs tests/Koh.Lsp.Tests/InlayHintTests.cs
git commit -m "feat(lsp): add textDocument/inlayHint with resolved-symbol lookup"
```

---

## Task 5: Argument Count Hint (LSP Signature Help)

**Milestone:** `textDocument/signatureHelp` shows the observed argument count for macro calls. Since RGBDS macros have no declared parameter list, the hint reflects the maximum argument count observed across call sites in open files. Macros called only in unopened files are not reflected.

**Prerequisite:** Plan 3 Task 1 (Macro Arity Metadata). The macro arity data must be available in the binding pipeline before this task can be implemented.

**Design choices (explicit):**
- Trigger characters: only `,` — NOT space. Space triggers too broadly. First invocation after the macro name requires manual Ctrl+Shift+Space.
- Active parameter: determined by counting `CommaToken`s before cursor within the `MacroCall` node. This matches `CollectMacroArgs` for simple cases but does NOT handle: (1) commas inside angle-bracket-quoted args `<a, b>`, (2) `\,` escaped commas. `CollectMacroArgs` handles these with paren-depth tracking and escape support; the simple comma-count does not. Fixing this would require porting the full `CollectMacroArgs` token-walking logic. Deferred for v1.
- Labels show as `\1`, `\2`, etc. since RGBDS has no named parameters.

**Files:**
- Modify: `src/Koh.Lsp/KohLanguageServer.cs`
- Test: `tests/Koh.Lsp.Tests/SignatureHelpTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using Koh.Lsp;

namespace Koh.Lsp.Tests;

public class SignatureHelpTests
{
    [Test]
    public async Task SignatureHelp_MacroCall_ShowsArgCount()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "my_add: MACRO\n    ld a, \\1\n    add a, \\2\nENDM\n" +
            "    my_add b, c\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.SignatureHelp(TestHelpers.PositionParams(
            "file:///test.asm", line: 5, character: 11));

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Signatures.Length).IsEqualTo(1);
        await Assert.That(result.Signatures[0].Parameters!.Length).IsEqualTo(2);
    }

    [Test]
    public async Task SignatureHelp_SecondArg_ActiveIndexIs1()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "my_add: MACRO\n    ld a, \\1\n    add a, \\2\nENDM\n" +
            "    my_add b, c\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.SignatureHelp(TestHelpers.PositionParams(
            "file:///test.asm", line: 5, character: 14));

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.ActiveParameter).IsEqualTo(1u);
    }

    [Test]
    public async Task SignatureHelp_NotInMacroCall_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\nnop\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.SignatureHelp(TestHelpers.PositionParams(
            "file:///test.asm", line: 1, character: 0));

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task SignatureHelp_ZeroArgMacro_ShowsEmptyParams()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n" +
            "init: MACRO\n    xor a\nENDM\n" +
            "    init\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.SignatureHelp(TestHelpers.PositionParams(
            "file:///test.asm", line: 4, character: 4));

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Signatures[0].Parameters!.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SignatureHelp_UndefinedMacro_ReturnsNull()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm",
            "SECTION \"Main\", ROM0\n    unknown_macro a, b\n");

        var server = TestHelpers.CreateServer(ws);
        var result = server.SignatureHelp(TestHelpers.PositionParams(
            "file:///test.asm", line: 1, character: 18));

        await Assert.That(result).IsNull();
    }
}
```

- [ ] **Step 2: Register capability (trigger: `,` only) and implement handler**

Register `SignatureHelpOptions` with `TriggerCharacters = new[] { "," }`.

Handler implementation:
1. Find the token at cursor via `tree.Root.FindToken(offset)`
2. Walk up from `token.Parent` looking for a `MacroCall` ancestor — if none found, return null
3. Get the macro name: first `IdentifierToken` child of the `MacroCall` node
4. Look up arity: `workspace.GetMacroArities()?.TryGetValue(name, out var arity)` — if not found (undefined macro or never-called macro), return null
5. Count `CommaToken`s in the `MacroCall` node's direct children that appear before the cursor offset — this is the active parameter index
6. Build `SignatureHelp` with `arity` parameter labels (`\1` through `\N`) and `ActiveParameter = min(commaCount, arity - 1)`

- [ ] **Step 3: Run tests, verify pass**

```bash
cd /c/projekty/koh && dotnet test tests/Koh.Lsp.Tests
```

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Lsp/KohLanguageServer.cs tests/Koh.Lsp.Tests/SignatureHelpTests.cs
git commit -m "feat(lsp): add textDocument/signatureHelp for macro argument count hints"
```

---

## Task 6: Manual Local Verification

**Milestone:** All LSP features manually verified end-to-end in VS Code.

**What this is:** A structured manual checklist. Not automated testing.

- [ ] **Step 1: Build the LSP server**

```bash
cd /c/projekty/koh
dotnet publish src/Koh.Lsp -c Release -o editors/vscode/server
```

- [ ] **Step 2: Build the VS Code extension**

```bash
cd /c/projekty/koh/editors/vscode
npm install && npm run compile
```

- [ ] **Step 3: Verify manually**

Open VS Code in extension development mode. Verification checklist:

1. **Diagnostics** — syntax error → red squiggle
2. **Hover** — `nop` → instruction details; label → symbol info
3. **Go-to-definition** — Ctrl+Click label reference → jumps to declaration
4. **Find references** — right-click label → Find All References
5. **Completion** — type `ld` → suggestions appear
6. **Document symbols** — Ctrl+Shift+O → labels, constants, sections
7. **Rename** — F2 on label → renames across file
8. **Semantic tokens** — verify with: disable TMLanguage grammar to confirm LSP-driven highlighting. **Note:** VS Code may need `"editor.semanticHighlighting.enabled": true` in settings.
9. **Inlay hints** — verify with: `"editor.inlayHints.enabled": "on"` in settings. EQU constant reference shows inline value.
10. **Signature help** — type macro call, press `,` → parameter info appears. Also try Ctrl+Shift+Space after macro name.
11. **Rename rejection — keyword** — F2 on `nop` → should fail or show error
12. **Rename rejection — register** — F2 on `a` in `ld a, b` → should fail
13. **Macro rename** — F2 on macro call name → renames definition and all call sites
14. **EQUS rename** — F2 on an EQUS constant → renames correctly
15. **EQUS inlay hint** — reference to EQUS constant → no numeric hint (string constants don't have numeric values)
16. **Macro rename collision** — rename macro to existing label name → should be rejected

**Settings required:** Ensure VS Code has `"editor.inlayHints.enabled": "on"` and `"editor.semanticHighlighting.enabled": true` in settings. Without these, inlay hints and semantic tokens won't appear regardless of server support.

- [ ] **Step 4: Fix issues, commit**

Common issues: URI format mismatches on Windows, VS Code settings as noted above.

---
