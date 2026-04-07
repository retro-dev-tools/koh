# Plan 2 — LSP Symbol Features

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement correct workspace-wide LSP symbol features on top of Plan 1. All symbol-driven editor behavior in this plan must be semantic, owner-aware, and based on resolved symbols, never on text matching.

**Dependencies:**

* **Plan 1 — Compiler Identity Foundation** must be complete.
* **Plan 3 Task 1 — Macro Arity Metadata** must be complete before Signature Help.

**Required correctness rule:** If implementation reveals any compiler, workspace, or LSP prerequisite bug that prevents correct behavior, fixing that bug is **in scope for this plan**. Do not document it as a known limitation, workaround, or future task.

**Exit criteria:**

* Rename is workspace-wide and semantic
* Owner-local symbols rename across all loaded documents belonging to the same owner
* Exported symbols rename across all loaded documents where the resolved symbol has the same `SymbolId`
* Semantic tokens, inlay hints, and signature help are driven by correct semantic/compiler state where required
* No text-only matching is used for rename/reference-sensitive behavior
* No knowingly incorrect heuristics remain in final behavior
* No temporary APIs, temporary storage paths, or temporary behavior remain
* Every documented rule has direct automated test coverage
* Manual verification only confirms already-correct behavior; it does not compensate for missing automated coverage

---

## Phase Overview

| Task | Milestone                      | You can now...                                                                                                     |
| ---- | ------------------------------ | ------------------------------------------------------------------------------------------------------------------ |
| 1    | Semantic Symbol Infrastructure | Resolve rename/reference targets correctly across the workspace using `SymbolId` and owner-aware compilation state |
| 2    | Rename                         | Rename labels, constants, string constants, and macros correctly across all affected loaded documents              |
| 3    | Semantic Tokens                | Provide stable syntax/semantic coloring through explicit classification rules                                      |
| 4    | Inlay Hints                    | Show correct numeric hints for resolved labels and constants only                                                  |
| 5    | Signature Help                 | Show correct macro argument help with correct active-parameter computation                                         |
| 6    | Manual Verification            | Confirm end-to-end behavior in VS Code after automated coverage is green                                           |

---

## Global Rules

**Semantic-only rule.** Any behavior that depends on “what symbol this token means” must resolve the symbol and act on `SymbolId`, never on raw text.

**Workspace-wide rule.** Features in this plan operate across all loaded documents in the workspace, not just the current file.

**Owner-awareness rule.**

* Owner-local symbols affect all loaded documents belonging to the same owner
* Exported symbols affect all loaded documents where the resolved symbol matches the same exported `SymbolId`
* Same-name symbols in different owners must never be conflated

**No shortcut rule.** Do not ship known-bad heuristics such as:

* file-scoped rename
* raw text occurrence replacement
* comma counting that disagrees with real macro argument parsing
* partially semantic collision checks
* fallback-to-name-match after semantic resolution fails

**Scope expansion rule.** If a task exposes a missing workspace relation, stale compilation update bug, include-owner tracking bug, token resolution bug, or protocol wiring bug, extend that task and fix it there.

---

## Task 1: Semantic Symbol Infrastructure

**Milestone:** The LSP has one correct semantic-symbol pipeline for target resolution, occurrence discovery, owner-aware workspace traversal, and symbol validation.

**Files:**

* Create: `src/Koh.Lsp/SymbolFinder.cs`
* Create: `tests/Koh.Lsp.Tests/TestHelpers.cs`
* Modify: `src/Koh.Lsp/Workspace.cs`
* Modify: `src/Koh.Lsp/KohLanguageServer.cs` if shared helpers/capabilities are needed
* Modify any additional LSP/compiler integration files required for correctness
* Test: `tests/Koh.Lsp.Tests/SymbolFinderTests.cs`

### Required behavior

* Symbol target resolution must use compiler semantic models and `ResolveSymbol`
* Occurrence matching must use `SymbolId`
* Workspace traversal must understand owner relationships across loaded root/include documents
* If current `Workspace` cannot map loaded documents to the correct owner compilation context, fix it here
* If current compilation refresh/update logic can yield stale or wrong semantic results, fix it here
* Include-owned documents must participate in same-owner rename/reference discovery correctly
* Export directives must participate as semantic references to the exported symbol
* Collision checks must distinguish owner-local visibility from exported visibility correctly

### Step checklist

* [ ] **Step 1: Create `TestHelpers`**

Create helper builders for:

* workspace creation

* server creation

* rename params

* position params

* semantic tokens params

* inlay hint params

* signature help params

* [ ] **Step 2: Create `SymbolFinder`**

`SymbolFinder` must become the single authority for:

* resolving rename/reference targets
* finding all semantic occurrences
* context gating
* collision checks
* symbol-kind gating
* name validation helpers used by rename

Suggested shape:

```csharp
namespace Koh.Lsp;

internal sealed class SymbolFinder
{
    internal sealed record ResolvedSymbol(
        Symbol Symbol,
        SyntaxToken Token,
        string Uri,
        string OwnerId,
        bool IsDeclaration);

    public ResolvedSymbol? ResolveAt(Workspace workspace, string uri, int offset);

    public IReadOnlyList<ResolvedSymbol> FindAllOccurrences(
        Workspace workspace,
        ResolvedSymbol target,
        bool includeDeclarations = true);

    public string? ValidateRename(
        Workspace workspace,
        ResolvedSymbol target,
        string newName);
}
```

* [ ] **Step 3: Implement correct context gating**

Accepted declaration/reference contexts must be explicit and reused by later tasks. At minimum:

* declaration: `LabelDeclaration`, `SymbolDirective`
* reference: `NameExpression`, `LabelOperand`, `MacroCall`

If current syntax shapes require additional contexts for correctness, add them here.

* [ ] **Step 4: Implement `ResolveAt`**

Rules:

* find token at offset

* reject non-identifier / non-local-label tokens

* require symbol-bearing ancestor

* resolve via compilation semantic model

* use `ResolveSymbol(token.Text, token.Span.Start)` for correct local-label and owner-aware resolution

* return null if semantic resolution fails

* never fall back to text search

* [ ] **Step 5: Implement occurrence discovery**

Rules:

* for every loaded document, walk candidate identifier tokens in semantic contexts

* resolve each candidate semantically

* keep only exact `SymbolId` matches

* dedupe by `(uri, span.Start)`

* include declaration/reference locations based on call option

* export directives must be included if they semantically refer to the same symbol

* [ ] **Step 6: Fix workspace ownership plumbing if needed**

If current workspace does not expose enough information to search all documents belonging to the same owner correctly, add it here. The final system must support:

* root file + included files participating in one owner-local symbol space

* exported symbol discovery across all loaded documents

* fresh compilation rebuilds after document changes

* [ ] **Step 7: Implement rename validation primitives**

Validation must support:

* lexical identifier validity

* keyword rejection via `Lexer.IsKeyword`

* register-name rejection if registers are not renameable symbols

* local/global form preservation

* owner-local collision checks

* exported collision checks

* semantic collision checks against actual resolved symbols, not names alone

* [ ] **Step 8: Write failing tests**

Create `tests/Koh.Lsp.Tests/SymbolFinderTests.cs` with coverage for all infrastructure rules.

Minimum required tests:

```csharp
public class SymbolFinderTests
{
    [Test]
    public async Task ResolveAt_LocalLabel_UsesCorrectScope() { }

    [Test]
    public async Task ResolveAt_UnresolvedLookalike_ReturnsNull() { }

    [Test]
    public async Task FindAllOccurrences_OwnerLocalSymbol_StaysWithinOwner() { }

    [Test]
    public async Task FindAllOccurrences_ExportedSymbol_SpansAllLoadedDocuments() { }

    [Test]
    public async Task FindAllOccurrences_ShadowingOwnerLocal_DoesNotMatchExported() { }

    [Test]
    public async Task FindAllOccurrences_SameTextDifferentOwners_DoesNotConflate() { }

    [Test]
    public async Task FindAllOccurrences_IncludeOwnedSymbol_FindsRootAndIncludedDocs() { }

    [Test]
    public async Task FindAllOccurrences_ExportDirective_IsIncludedForExportedSymbol() { }

    [Test]
    public async Task ValidateRename_OwnerLocalCollision_Rejected() { }

    [Test]
    public async Task ValidateRename_ExportedCollision_Rejected() { }

    [Test]
    public async Task ValidateRename_LocalToGlobal_Rejected() { }

    [Test]
    public async Task ValidateRename_GlobalToLocal_Rejected() { }
}
```

* [ ] **Step 9: Run tests**

```bash
cd /c/projekty/koh && dotnet test tests/Koh.Lsp.Tests
```

* [ ] **Step 10: Commit**

```bash
git add src/Koh.Lsp/SymbolFinder.cs src/Koh.Lsp/Workspace.cs src/Koh.Lsp/KohLanguageServer.cs tests/Koh.Lsp.Tests/TestHelpers.cs tests/Koh.Lsp.Tests/SymbolFinderTests.cs
git commit -m "feat(lsp): add semantic symbol infrastructure for workspace-wide symbol resolution"
```

---

## Task 2: Rename

**Milestone:** `textDocument/prepareRename` and `textDocument/rename` are fully semantic and workspace-wide.

**Files:**

* Modify: `src/Koh.Lsp/KohLanguageServer.cs`
* Modify any supporting LSP files required for protocol correctness
* Test: `tests/Koh.Lsp.Tests/RenameTests.cs`

### Required behavior

* Rename must update declarations and references for the same semantic symbol only
* Owner-local rename affects all loaded docs in that owner
* Exported rename affects all loaded docs whose resolved symbol matches the exported `SymbolId`
* Rename must update export directives when they refer to the renamed symbol
* Rename must not touch unresolved lookalikes
* Rename must not touch same-name symbols in other owners
* Rename must not touch shadowing symbols
* All edits must be produced from semantic occurrence discovery, never from text search

### Step checklist

* [ ] **Step 1: Implement `PrepareRename`**

Rules:

* use `SymbolFinder.ResolveAt`

* reject unresolved targets

* reject non-renameable kinds

* return exact token range of the resolved target

* [ ] **Step 2: Implement `Rename`**

Rules:

* resolve target semantically

* validate new name semantically and lexically

* collect occurrences from `SymbolFinder.FindAllOccurrences`

* build `WorkspaceEdit` grouped by URI

* no text-only replacement path allowed

* [ ] **Step 3: Write failing tests**

Create `tests/Koh.Lsp.Tests/RenameTests.cs`.

Minimum required tests:

```csharp
public class RenameTests
{
    [Test]
    public async Task Rename_GlobalLabel_RenamesDeclarationAndAllReferences() { }

    [Test]
    public async Task Rename_LocalLabel_RenamesOnlyCurrentScopedSymbol() { }

    [Test]
    public async Task Rename_Constant_RenamesDeclarationAndReferences() { }

    [Test]
    public async Task Rename_StringConstant_RenamesDeclarationAndReferences() { }

    [Test]
    public async Task Rename_Macro_RenamesDefinitionAndCallSites() { }

    [Test]
    public async Task Rename_FromReference_RenamesDeclarationToo() { }

    [Test]
    public async Task Rename_ExportedSymbol_UpdatesExportDirective() { }

    [Test]
    public async Task Rename_OwnerLocalAcrossIncludeRootAndIncludedDocs_Works() { }

    [Test]
    public async Task Rename_ExportedAcrossMultipleLoadedDocs_Works() { }

    [Test]
    public async Task Rename_SameNameDifferentOwner_NotTouched() { }

    [Test]
    public async Task Rename_ShadowingOwnerLocal_NotTouchedWhenRenamingExported() { }

    [Test]
    public async Task Rename_UnresolvedLookalike_NotTouched() { }

    [Test]
    public async Task PrepareRename_Keyword_ReturnsNull() { }

    [Test]
    public async Task PrepareRename_Register_ReturnsNull() { }

    [Test]
    public async Task Rename_ToReservedKeyword_ReturnsNull() { }

    [Test]
    public async Task Rename_ToInvalidIdentifier_ReturnsNull() { }

    [Test]
    public async Task Rename_LocalToGlobalForm_ReturnsNull() { }

    [Test]
    public async Task Rename_GlobalToLocalForm_ReturnsNull() { }

    [Test]
    public async Task Rename_ToExistingOwnerLocalCollision_ReturnsNull() { }

    [Test]
    public async Task Rename_ToExistingExportedCollision_ReturnsNull() { }

    [Test]
    public async Task Rename_LocalToSameNameInDifferentScope_Allowed() { }
}
```

* [ ] **Step 4: Run tests**

```bash
cd /c/projekty/koh && dotnet test tests/Koh.Lsp.Tests
```

* [ ] **Step 5: Commit**

```bash
git add src/Koh.Lsp/KohLanguageServer.cs tests/Koh.Lsp.Tests/RenameTests.cs
git commit -m "feat(lsp): implement semantic workspace-wide rename"
```

---

## Task 3: Semantic Tokens

**Milestone:** `textDocument/semanticTokens/full` returns stable, explicit token classification with guard coverage for `SyntaxKind`.

**Files:**

* Create: `src/Koh.Lsp/SemanticTokenEncoder.cs`
* Modify: `src/Koh.Lsp/KohLanguageServer.cs`
* Test: `tests/Koh.Lsp.Tests/SemanticTokenTests.cs`
* Test: `tests/Koh.Core.Tests/Syntax/SyntaxKindCoverageTests.cs`

### Required behavior

* Classification rules must be explicit
* No silent omissions for new token kinds
* If a token kind is intentionally unclassified, it must be explicitly listed
* If current coarse identifier coloring is acceptable as a product rule, state it as the rule and test it
* If semantic differentiation is needed for consistency, implement it here rather than documenting a simplification

### Step checklist

* [ ] **Step 1: Implement `SemanticTokenEncoder`**

Must expose:

* token type constants

* explicit classification sets

* `AllClassifiedKinds`

* `IntentionallyUnclassifiedKinds`

* encoder from syntax tree/token stream to LSP delta-encoded array

* [ ] **Step 2: Register semantic tokens capability and handler**

Add server capability and handler in `KohLanguageServer`.

* [ ] **Step 3: Write failing tests**

Create `tests/Koh.Lsp.Tests/SemanticTokenTests.cs`.

Minimum coverage:

* instruction keyword

* register

* directive

* number

* string

* comment

* local label

* operator

* condition flag

* identifier classification rule

* [ ] **Step 4: Add `SyntaxKind` coverage guard**

Create `tests/Koh.Core.Tests/Syntax/SyntaxKindCoverageTests.cs` so new token kinds cannot be added silently without classification or explicit exclusion.

* [ ] **Step 5: Run tests**

```bash
cd /c/projekty/koh && dotnet test
```

* [ ] **Step 6: Commit**

```bash
git add src/Koh.Lsp/SemanticTokenEncoder.cs src/Koh.Lsp/KohLanguageServer.cs tests/Koh.Lsp.Tests/SemanticTokenTests.cs tests/Koh.Core.Tests/Syntax/SyntaxKindCoverageTests.cs
git commit -m "feat(lsp): add semantic tokens with explicit syntax kind coverage"
```

---

## Task 4: Inlay Hints

**Milestone:** `textDocument/inlayHint` emits correct numeric hints only for resolved label and constant references.

**Files:**

* Modify: `src/Koh.Lsp/KohLanguageServer.cs`
* Modify `src/Koh.Lsp/SymbolFinder.cs` if shared context-gating helpers are needed
* Test: `tests/Koh.Lsp.Tests/InlayHintTests.cs`

### Required behavior

* Hints only on references, never declarations
* Hints only for resolved `Label` and `Constant`
* No hints for `StringConstant`
* No hints for `Macro`
* Local labels must resolve with correct scope
* Duplicate hints must not be emitted
* If current token walking or range handling is insufficient, fix it here

### Step checklist

* [ ] **Step 1: Implement inlay hint handler**

Rules:

* iterate identifier tokens in requested range

* require reference context

* reject declaration context

* resolve symbol semantically

* filter to `Label` / `Constant`

* dedupe by `(uri, span.Start)`

* emit value text in correct format

* [ ] **Step 2: Write failing tests**

Create `tests/Koh.Lsp.Tests/InlayHintTests.cs`.

Minimum coverage:

* constant reference gets hint

* constant declaration gets none

* label declaration gets none

* unresolved symbol gets none

* keyword gets none

* EQUS gets none

* macro declaration/call gets none

* label reference gets address hint

* constant gets hex + decimal

* local labels use correct scope

* no duplicates

* [ ] **Step 3: Run tests**

```bash
cd /c/projekty/koh && dotnet test tests/Koh.Lsp.Tests
```

* [ ] **Step 4: Commit**

```bash
git add src/Koh.Lsp/KohLanguageServer.cs src/Koh.Lsp/SymbolFinder.cs tests/Koh.Lsp.Tests/InlayHintTests.cs
git commit -m "feat(lsp): add semantic inlay hints for resolved labels and constants"
```

---

## Task 5: Signature Help

**Milestone:** `textDocument/signatureHelp` provides correct macro argument help and correct active-parameter tracking.

**Depends on:** Plan 3 Task 1 complete and correctly integrated into current workspace compilation updates.

**Files:**

* Modify: `src/Koh.Lsp/KohLanguageServer.cs`
* Modify compiler/LSP integration files if needed to surface macro arity metadata correctly
* Extract reusable argument-boundary logic if needed
* Test: `tests/Koh.Lsp.Tests/SignatureHelpTests.cs`

### Required behavior

* Macro signature help must use actual macro arity metadata from current workspace compilation
* Active parameter computation must follow the same argument-boundary rules as macro parsing
* Do not use naive comma counting if it disagrees with macro argument parsing
* If current macro argument parsing logic is not reusable from LSP, extract shared logic in this task
* Undefined or semantically unresolved macros return null

### Step checklist

* [ ] **Step 1: Register signature help capability**

Use trigger characters required by the final behavior. If `,` is sufficient, use it. If additional triggers are needed for correct UX, add them.

* [ ] **Step 2: Implement signature help handler**

Rules:

* find enclosing `MacroCall`

* resolve macro symbol semantically

* get macro arity from current workspace compilation state

* compute active parameter using the same argument-boundary logic as real macro argument parsing

* build parameter list `\1` .. `\N`

* [ ] **Step 3: Extract shared argument-boundary logic if needed**

If current parser/expander logic cannot be reused directly, create a shared helper so signature help and macro parsing agree.

* [ ] **Step 4: Write failing tests**

Create `tests/Koh.Lsp.Tests/SignatureHelpTests.cs`.

Minimum coverage:

* macro call shows correct arity

* second arg reports active parameter 1

* zero-arg macro shows zero params

* undefined macro returns null

* not-in-macro-call returns null

* comma inside nested/grouped argument does not break active parameter

* escaped comma handling matches macro parser

* angle-bracket/group syntax matches macro parser if language supports it

* [ ] **Step 5: Run tests**

```bash
cd /c/projekty/koh && dotnet test tests/Koh.Lsp.Tests
```

* [ ] **Step 6: Commit**

```bash
git add src/Koh.Lsp/KohLanguageServer.cs tests/Koh.Lsp.Tests/SignatureHelpTests.cs
git commit -m "feat(lsp): add correct macro signature help using shared argument parsing rules"
```

---

## Task 6: Manual Verification

**Milestone:** End-to-end behavior is confirmed in VS Code after automated coverage is green.

### Step checklist

* [ ] **Step 1: Build the LSP server**

```bash
cd /c/projekty/koh
dotnet publish src/Koh.Lsp -c Release -o editors/vscode/server
```

* [ ] **Step 2: Build the VS Code extension**

```bash
cd /c/projekty/koh/editors/vscode
npm install && npm run compile
```

* [ ] **Step 3: Verify manually**

Confirm:

1. Rename works from declaration and reference
2. Owner-local rename stays within same owner across loaded documents
3. Exported rename updates all loaded matching references and export directives
4. Shadowing symbols are not touched
5. Semantic tokens render correctly
6. Inlay hints show only for labels/constants
7. Signature help tracks macro arguments correctly
8. Rejections for invalid rename targets behave correctly

* [ ] **Step 4: Fix any real bug discovered**

Any bug found here is in scope for the relevant earlier task and must be fixed properly, not documented as a limitation.

* [ ] **Step 5: Commit**

```bash
git add .
git commit -m "test(lsp): verify workspace-wide symbol features end-to-end"
```

---

## Done Checklist

Before considering this plan complete, verify all are true:

* [ ] Rename uses semantic resolution and `SymbolId` matching only
* [ ] Rename is workspace-wide, not file-scoped
* [ ] Owner-local rename reaches all loaded documents for the same owner
* [ ] Exported rename reaches all loaded documents resolving to the same exported symbol
* [ ] Export directives are updated when semantically tied to the symbol
* [ ] Same-name symbols in other owners are untouched
* [ ] Shadowing symbols are untouched
* [ ] Unresolved lookalikes are untouched
* [ ] Semantic token coverage is explicit and guarded
* [ ] Inlay hints use semantic resolution and correct kind filtering
* [ ] Signature help uses correct macro argument-boundary logic
* [ ] No known incorrect heuristic remains
* [ ] Every documented rule has direct automated test coverage
