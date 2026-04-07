# Plan 1 — Compiler Identity Foundation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish the final compiler symbol identity model. Task 3 is the first complete version of that model: owner-local storage, exported storage, `SymbolId`, `SymbolResolutionContext`, and final symbol-definition APIs for all symbol kinds. Task 1 adds authoritative post-binding symbol resolution by raw name and position. Task 2 adds the `Macro` symbol kind.

**Non-goals:** This plan does NOT include LSP handlers, rename, semantic tokens, inlay hints, signature help, manual verification, AOT, incremental parsing, linker diagnostics, or cross-owner exported reference resolution during binding.

**Spec:** `docs/superpowers/specs/2026-03-25-koh-assembler-design.md`
**Original plan:** `docs/superpowers/plans/2026-03-25-koh-implementation-plan.md`
**Parent index:** `docs/superpowers/plans/2026-04-02-koh-completion-plan.md`

**Exit criteria:**

* Compiler symbol identity and visibility model is internally consistent
* `SymbolId` is the canonical semantic identity of every defined symbol
* `ResolveSymbol` is the authoritative post-binding symbol-resolution API
* Owner-local, exported, INCLUDE, and macro semantics are proven by tests
* No global-only non-exported storage path exists for labels, constants, string constants, or macros
* No tests in this plan depend on LSP, workspace, or editor behavior
* Exported cross-owner reference resolution is out of scope and not claimed
* `GetSymbol`, `GetDeclaredSymbol`, and `LookupSymbols` follow the owner-aware model defined here
* Every rule in Duplicate and Collision Rules has direct test coverage
* Every diagnostic promised in prose has at least one test

---

## Phase Overview

| Task | Milestone                        | You can now...                                                                                          |
| ---- | -------------------------------- | ------------------------------------------------------------------------------------------------------- |
| 1    | Position-aware symbol resolution | `ResolveSymbol` correctly resolves symbols by raw name and source position post-binding                 |
| 2    | Macro Symbol Kind                | The compiler model distinguishes macros from labels/constants/string constants at the symbol-kind level |
| 3    | Symbol Ownership Model           | Full owner-local/exported identity, storage, and definition APIs for all symbol kinds                   |

---

## Design Constraints

**`SymbolId` is the canonical semantic identity of a symbol.** `Symbol.SymbolId` = `(OwnerId, QualifiedName)` for owner-local symbols, or `(null, QualifiedName)` for exported symbols in the exported namespace. `Symbol.Name` is for display and lookup keys only.

**`OwnerId` is an opaque root-translation-unit identifier.** Initially it is the root `SyntaxTree.Text.FilePath`, but code must not assume path semantics. Treat it as an opaque `string`.

**Owner = root translation unit, not the current included file.** RGBDS `INCLUDE` is textual inclusion. All symbols defined while binding one root tree belong to that root tree’s owner, even if the syntax originated in an included file. Ownership uses `OwnerId`. Diagnostics use actual source file path.

**`SymbolKind` remains semantically distinct.** Labels, constants (`EQU`), string constants (`EQUS`), and macros are separate semantic kinds.

**Single shared compilation model is preserved.** One `Compilation`, one `Binder`, one `SymbolTable` per compile. `SemanticModel` is per syntax tree and shares the same `BindingResult`.

**Root file path requirement.**

* Multi-tree compilation requires every root `SyntaxTree.Text.FilePath` to be non-null and non-empty; otherwise `Compilation.Create(...)` throws `ArgumentException`.
* Single-tree compilation with null or empty file path is allowed and uses synthetic owner id `"<anonymous>"`.

**Local-label parser invariant.** This plan assumes local labels become qualified names containing `.` and no other symbol names contain `.`. If parser rules change, `ResolveSymbol` and `LookupSymbols` must be updated.

**Authoritative post-binding API.**

* `ResolveSymbol(rawName, position)` is the authoritative post-binding resolution API.
* `GetSymbol` and `GetDeclaredSymbol` are node-based helpers layered on top of the owner-aware model.
* `LookupSymbols(position)` reflects symbols visible in the semantic model’s owner and current local-label scope.

---

## Export Storage Rules

These define storage and identity behavior only. They do **not** claim cross-owner exported reference resolution during binding.

1. **Non-exported symbols are owner-local.** Stored by `(OwnerId, QualifiedName)`.

2. **`EXPORT` promotes to exported storage.** The symbol is removed from owner-local storage, added to exported storage, `Visibility` becomes `Exported`, and `OwnerId` becomes `null`.

3. **Duplicate exports from different owners produce an error diagnostic** at the second `EXPORT` directive site:
   `"Exported symbol 'name' is already defined by another translation unit."`

4. **Owner-local symbols shadow exported symbols within the same owner** during lookup.

5. **Macros cannot be exported.** Attempting to export a macro produces a diagnostic at the `EXPORT` directive site:
   `"Cannot export macro 'name' — macros are not exportable."`

6. **Local labels cannot be exported.** Attempting to export a local label produces a diagnostic at the `EXPORT` directive site:
   `"Cannot export local label 'name'."`

7. **String constants (`EQUS`) are exportable in this model.** Export preserves kind and gives exported identity `(null, QualifiedName)`.

8. **Undefined export targets are invalid.** Attempting to export an undefined symbol produces a diagnostic at the `EXPORT` directive site:
   `"Cannot export undefined symbol 'name'."`

---

## Duplicate and Collision Rules

* **Same owner + same qualified name** across any combination of label / constant / string constant / macro → duplicate definition diagnostic at the conflicting definition site
* **Different owners + non-exported** → no collision
* **Exported vs exported** same name from different owners → duplicate export diagnostic at the second `EXPORT` directive site
* **Owner-local vs exported** same name → allowed; owner-local shadows exported within that owner

---

## Verified API Inventory

| API                                                                    | Exists            | Notes                                                                      |
| ---------------------------------------------------------------------- | ----------------- | -------------------------------------------------------------------------- |
| `Compilation.GetSemanticModel(SyntaxTree)`                             | Yes               | Returns new `SemanticModel` sharing same binding result                    |
| `Compilation.Create(params SyntaxTree[])`                              | Yes               | Task 3 makes it delegate through resolver-aware path                       |
| `SemanticModel.GetSymbol(SyntaxNode)`                                  | Yes               | Handles `NameExpression`, `LabelOperand`                                   |
| `SemanticModel.GetDeclaredSymbol(SyntaxNode)`                          | Yes               | Handles `LabelDeclaration`, `SymbolDirective`                              |
| `SemanticModel.LookupSymbols(int)`                                     | Yes               | Task 3 makes it owner-aware                                                |
| `SyntaxTree.Create(SourceText, SyntaxNode, IReadOnlyList<Diagnostic>)` | Yes               | `internal`                                                                 |
| `SyntaxNode.Position`                                                  | Yes               | Absolute offset                                                            |
| `SyntaxNode.FindToken(int)`                                            | Yes               | Recursive token search                                                     |
| `SyntaxNode.ChildNodes/Tokens/NodesAndTokens()`                        | Yes               | All three exist                                                            |
| `SourceText.WithChanges(TextChange)`                                   | Yes               | Returns new `SourceText`                                                   |
| `SymbolTable.Lookup(string)`                                           | Yes               | Current flat lookup; Task 3 replaces compiler usage with context-aware API |
| `ISourceFileResolver`                                                  | Yes               | INCLUDE/INCBIN resolution                                                  |
| `VirtualFileResolver`                                                  | Yes               | In-memory resolver with `AddTextFile` / `AddBinaryFile`                    |
| `Binder` constructor                                                   | Yes               | Accepts `ISourceFileResolver?`                                             |
| `Lexer.Keywords`                                                       | `static readonly` | Thread-safe                                                                |

---

## Task 1: Position-aware symbol resolution

**Milestone:** `ResolveSymbol` correctly resolves symbols by raw name and source position post-binding. `LookupQualified(string)` provides direct qualified-name lookup without using the stale global anchor.

**Files:**

* Modify: `src/Koh.Core/SemanticModel.cs`

* Modify: `src/Koh.Core/Symbols/SymbolTable.cs`

* Modify: `src/Koh.Core/Syntax/Lexer.cs`

* Test: `tests/Koh.Core.Tests/SemanticModelTests.cs`

* [ ] **Step 1: Write failing tests**

```csharp
[Test]
public async Task ResolveSymbol_LocalLabel_UsesPositionScope()
{
    var text =
        "SECTION \"Main\", ROM0\n" +
        "funcA:\n.done:\n    jr .done\n" +
        "funcB:\n.done:\n    jr .done\n";
    var tree = SyntaxTree.Parse(text);
    var compilation = Compilation.Create(tree);
    var model = compilation.GetSemanticModel(tree);

    int posA = text.IndexOf("jr .done");
    var symA = model.ResolveSymbol(".done", posA);
    await Assert.That(symA).IsNotNull();
    await Assert.That(symA!.Name).IsEqualTo("funcA.done");

    int posB = text.LastIndexOf("jr .done");
    var symB = model.ResolveSymbol(".done", posB);
    await Assert.That(symB).IsNotNull();
    await Assert.That(symB!.Name).IsEqualTo("funcB.done");
}

[Test]
public async Task ResolveSymbol_GlobalLabel_WorksAnywhere()
{
    var text = "SECTION \"Main\", ROM0\nmain:\n    jp main\n";
    var tree = SyntaxTree.Parse(text);
    var compilation = Compilation.Create(tree);
    var model = compilation.GetSemanticModel(tree);

    int pos = text.IndexOf("jp main");
    var sym = model.ResolveSymbol("main", pos);
    await Assert.That(sym).IsNotNull();
    await Assert.That(sym!.Name).IsEqualTo("main");
}

[Test]
public async Task ResolveSymbol_LocalLabelBeforeFirstGlobal_ReturnsNull()
{
    var text = "SECTION \"Main\", ROM0\n.orphan:\n    nop\nmain:\n";
    var tree = SyntaxTree.Parse(text);
    var compilation = Compilation.Create(tree);
    var model = compilation.GetSemanticModel(tree);

    int pos = text.IndexOf("nop");
    var sym = model.ResolveSymbol(".orphan", pos);
    await Assert.That(sym).IsNull();
}

[Test]
public async Task ResolveSymbol_LocalLabelBeforeDefiningGlobal_ReturnsNull()
{
    var text = "SECTION \"Main\", ROM0\n    nop\nmain:\n.local:\n    jr .local\n";
    var tree = SyntaxTree.Parse(text);
    var compilation = Compilation.Create(tree);
    var model = compilation.GetSemanticModel(tree);

    int pos = text.IndexOf("nop");
    var sym = model.ResolveSymbol(".local", pos);
    await Assert.That(sym).IsNull();
}

[Test]
public async Task LabelDeclarations_AreTopLevelChildren()
{
    // If parser structure changes, ResolveSymbol must be updated.
    var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\nmain:\n.local:\n    nop\n");
    var labels = tree.Root.ChildNodes()
        .Where(n => n.Kind == SyntaxKind.LabelDeclaration)
        .ToList();

    await Assert.That(labels.Count).IsEqualTo(2);
}
```

* [ ] **Step 2: Add `SymbolTable.LookupQualified(string)`**

```csharp
public Symbol? LookupQualified(string qualifiedName)
{
    var dict = DictFor(qualifiedName);
    dict.TryGetValue(qualifiedName, out var sym);
    return sym;
}
```

* [ ] **Step 3: Implement `SemanticModel.ResolveSymbol`**

```csharp
public Symbol? ResolveSymbol(string rawName, int position)
{
    if (_result.Symbols == null)
        return null;

    if (rawName.StartsWith('.'))
    {
        string? scope = null;

        foreach (var node in _tree.Root.ChildNodes())
        {
            if (node.Position > position)
                break;

            if (node.Kind == SyntaxKind.LabelDeclaration)
            {
                var token = node.ChildTokens().FirstOrDefault();
                if (token != null && token.Kind == SyntaxKind.IdentifierToken)
                    scope = token.Text;
            }
        }

        if (scope == null)
            return null;

        return _result.Symbols.LookupQualified(scope + rawName);
    }

    return _result.Symbols.LookupQualified(rawName);
}
```

* [ ] **Step 4: Add `Lexer.IsKeyword`**

```csharp
public static bool IsKeyword(string text)
    => Keywords.ContainsKey(text);
```

* [ ] **Step 5: Run tests**

```bash
cd /c/projekty/koh && dotnet test tests/Koh.Core.Tests
```

* [ ] **Step 6: Commit**

```bash
git add src/Koh.Core/SemanticModel.cs src/Koh.Core/Symbols/SymbolTable.cs src/Koh.Core/Syntax/Lexer.cs tests/Koh.Core.Tests/SemanticModelTests.cs
git commit -m "feat: add position-aware ResolveSymbol and LookupQualified"
```

---

## Task 2: Macro Symbol Kind

**Milestone:** The compiler model distinguishes macros from labels, constants, and string constants at the symbol-kind level. No macro registration or storage changes happen in this task.

**Files:**

* Modify: `src/Koh.Core/Symbols/Symbol.cs`

* Test: `tests/Koh.Core.Tests/Symbols/MacroKindTests.cs`

* [ ] **Step 1: Add `Macro` to `SymbolKind`**

```csharp
public enum SymbolKind
{
    Label,
    Constant,
    StringConstant,
    Macro,
}
```

* [ ] **Step 2: Write tests**

```csharp
[Test]
public async Task MacroKind_IsDistinctFromOtherKinds()
{
    await Assert.That(SymbolKind.Macro).IsNotEqualTo(SymbolKind.Label);
    await Assert.That(SymbolKind.Macro).IsNotEqualTo(SymbolKind.Constant);
    await Assert.That(SymbolKind.Macro).IsNotEqualTo(SymbolKind.StringConstant);
}

[Test]
public async Task MacroKind_CanBeAssignedToSymbol()
{
    var sym = new Symbol("test_macro", SymbolKind.Macro);
    await Assert.That(sym.Kind).IsEqualTo(SymbolKind.Macro);
    await Assert.That(sym.Name).IsEqualTo("test_macro");
}
```

* [ ] **Step 3: Run tests**

```bash
cd /c/projekty/koh && dotnet test
```

* [ ] **Step 4: Commit**

```bash
git add src/Koh.Core/Symbols/Symbol.cs tests/Koh.Core.Tests/Symbols/MacroKindTests.cs
git commit -m "feat: add SymbolKind.Macro"
```

---

## Task 3: Symbol Ownership Model

**Milestone:** Full owner-local/exported identity, storage, and definition APIs for all symbol kinds including macros. `SymbolId` becomes canonical semantic identity. `SymbolResolutionContext` drives all lookup and definition. Macro registration is implemented here. `GetSymbol`, `GetDeclaredSymbol`, and `LookupSymbols` are aligned with the owner-aware model.

**Non-goals:**

* No per-file compilation redesign
* No linker behavior changes
* No cross-owner exported reference resolution during binding

**Files:**

* Create: `src/Koh.Core/Symbols/SymbolResolutionContext.cs`

* Modify: `src/Koh.Core/Symbols/Symbol.cs`

* Modify: `src/Koh.Core/Symbols/SymbolTable.cs`

* Modify: `src/Koh.Core/Binding/Binder.cs`

* Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`

* Modify: `src/Koh.Core/SemanticModel.cs`

* Modify: `src/Koh.Core/Compilation.cs`

* Test: `tests/Koh.Core.Tests/Symbols/SymbolOwnershipTests.cs`

* [ ] **Step 1: Create `SymbolResolutionContext`**

```csharp
namespace Koh.Core.Symbols;

public readonly record struct SymbolResolutionContext(
    string OwnerId,
    string? CurrentFilePath = null);
```

`OwnerId` is for identity and lookup. `CurrentFilePath` is diagnostics/source provenance only.

* [ ] **Step 2: Add `OwnerId` and `SymbolId` to `Symbol`**

```csharp
public string? OwnerId { get; internal set; }

public (string? OwnerId, string QualifiedName) SymbolId => (
    Visibility == SymbolVisibility.Exported ? null : OwnerId,
    Name);
```

* [ ] **Step 3: Refactor `SymbolTable` to final owner-aware storage**

**Final API surface:**

```csharp
public Symbol? Lookup(string rawName, SymbolResolutionContext context);
public Symbol? LookupQualified(string qualifiedName, SymbolResolutionContext context);
public Symbol? LookupExportedOnly(string qualifiedName);

public Symbol DefineLabel(string name, long pc, string? section,
    SyntaxNode? site, SymbolResolutionContext context);
public Symbol DefineConstant(string name, long value,
    SyntaxNode? site, SymbolResolutionContext context);
public Symbol DefineStringConstant(string name, string value,
    SyntaxNode? site, SymbolResolutionContext context);
public Symbol DefineMacro(string name, SyntaxNode? site,
    SymbolResolutionContext context);

public void PromoteExport(string rawName, SyntaxNode directiveSite,
    SymbolResolutionContext context);
```

**Storage:**

```csharp
private readonly Dictionary<(string OwnerId, string Name), Symbol> _ownerLocalSymbols = new();
// existing exported dictionary remains, but now stores exported symbols only
```

**Lookup rules:**

* `Lookup(rawName, context)`: qualify name, then owner-local first, exported second
* `LookupQualified(qualifiedName, context)`: owner-local first, exported second
* `LookupExportedOnly(qualifiedName)`: exported namespace only

**Definition rules:**

* All definitions use owner-local storage only
* Duplicate detection scope is `(context.OwnerId, qualifiedName)`
* Duplicate diagnostic is reported at the conflicting new definition site
* `sym.OwnerId = context.OwnerId`

**Export rules:**

* Missing owner-local symbol → diagnostic at directive site: `"Cannot export undefined symbol 'name'."`
* Local label → diagnostic at directive site: `"Cannot export local label 'name'."`
* Macro → diagnostic at directive site: `"Cannot export macro 'name' — macros are not exportable."`
* Duplicate exported name → diagnostic at directive site: `"Exported symbol 'name' is already defined by another translation unit."`
* Otherwise promote: remove from owner-local, set `Visibility = Exported`, set `OwnerId = null`, add to exported storage

**AllSymbols:**

* Returns owner-local + exported values

* Consumers must use `SymbolId` when identity matters

* [ ] **Step 4: Update `Binder` to create and pass root owner context**

```csharp
var ownerId = string.IsNullOrEmpty(tree.Text.FilePath) ? "<anonymous>" : tree.Text.FilePath;
var context = new SymbolResolutionContext(ownerId, tree.Text.FilePath);
```

Rules:

* `OwnerId` stays fixed for the whole root tree bind

* Included files do not change owner

* All definition/lookups in binder use `context`

* [ ] **Step 5: Register macros in `AssemblyExpander` using root owner context**

In macro-definition collection, register through:

```csharp
_symbols.DefineMacro(macroName, macroNode, context);
```

Requirements:

* context must be the root `SymbolResolutionContext`

* macro registration must never use included file path as owner

* the existing “label before MACRO — skip” behavior remains the only reason macro name is not also defined as a label

* [ ] **Step 6: Make resolver-aware `Compilation.Create` canonical**

Add a canonical public overload:

```csharp
public static Compilation Create(ISourceFileResolver resolver, params SyntaxTree[] trees)
```

Requirements:

* all public `Create(...)` overloads delegate through the resolver-aware path

* multi-tree compilation rejects null/empty root file paths with `ArgumentException`

* single-tree compilation with null/empty path uses synthetic owner `"<anonymous>"`

* do not introduce a separate semantic pipeline for resolver vs non-resolver cases

* [ ] **Step 7: Make `SemanticModel` owner-aware**

Store owner id once in constructor:

```csharp
private readonly string _ownerId;
```

with:

```csharp
_ownerId = string.IsNullOrEmpty(tree.Text.FilePath) ? "<anonymous>" : tree.Text.FilePath;
```

Update `ResolveSymbol` to use owner-aware `LookupQualified(qualifiedName, context)`.

* [ ] **Step 8: Align `GetSymbol`, `GetDeclaredSymbol`, and `LookupSymbols` with the owner-aware model**

**`GetSymbol`**

* For supported node kinds, extract the relevant identifier/local-label token
* Resolve using `ResolveSymbol(token.Text, token.Span.Start)`

**`GetDeclaredSymbol`**

* `LabelDeclaration`: resolve from declaration token via `ResolveSymbol`
* `SymbolDirective`: handle declaration forms explicitly by directive shape, not by undocumented token ordering assumptions
* add explicit handling/tests for:

  * `EQU`
  * `EQUS`
  * macro declaration label (`name: MACRO`)
* do not promise support for unsupported declaration shapes without tests

**`LookupSymbols(int)`**

* visible symbols are:

  * all exported symbols
  * owner-local symbols from the same owner only

* local-label visibility is filtered to current global scope only

* must not expose other owners’ locals

* [ ] **Step 9: Update binder `EXPORT` handling to use `PromoteExport`**

Requirement:

* binder must never silently ignore invalid export targets

* diagnostics must be emitted at the `EXPORT` directive site

* [ ] **Step 10: Write ownership/model tests**

```csharp
namespace Koh.Core.Tests.Symbols;

public class SymbolOwnershipTests
{
    [Test]
    public async Task SameNameDifferentOwners_NoDiagnostic()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\ncount:\n    nop\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\ncount EQU 42\n    ld a, count\n", "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SameNameDifferentOwners_ResolveToOwnSymbol()
    {
        var textB = "SECTION \"B\", ROM0\ncount EQU 42\n    ld a, count\n";
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\ncount:\n    nop\n", "a.asm"));
        var treeB = SyntaxTree.Parse(SourceText.From(textB, "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);
        var modelB = compilation.GetSemanticModel(treeB);

        int pos = textB.LastIndexOf("count");
        var sym = modelB.ResolveSymbol("count", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Constant);
        await Assert.That(sym.OwnerId).IsEqualTo("b.asm");
        await Assert.That(sym.SymbolId).IsEqualTo(("b.asm", "count"));
    }

    [Test]
    public async Task SameNameGlobalLabels_DifferentOwners_NoCollision()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nmain:\n    nop\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\nmain:\n    nop\n", "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DifferentOwners_SameNameConstants_NoCollision()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nfoo EQU 42\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\nfoo EQU 99\n", "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DifferentOwners_SameNameStringConstants_NoCollision()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nfoo EQUS \"hello\"\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\nfoo EQUS \"world\"\n", "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SameOwner_LabelAndConstant_SameName_ProducesDuplicate()
    {
        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nfoo:\nfoo EQU 42\n", "a.asm"));

        var compilation = Compilation.Create(tree);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task SameOwner_ConstantAndStringConstant_SameName_ProducesDuplicate()
    {
        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nfoo EQU 42\nfoo EQUS \"bar\"\n", "a.asm"));

        var compilation = Compilation.Create(tree);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task ExportedLabel_HasNullOwnerAndCorrectSymbolId()
    {
        var text = "SECTION \"A\", ROM0\nmain:\n    EXPORT main\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("EXPORT main");
        var sym = model.ResolveSymbol("main", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.OwnerId).IsNull();
        await Assert.That(sym.Visibility).IsEqualTo(SymbolVisibility.Exported);
        await Assert.That(sym.SymbolId).IsEqualTo(((string?)null, "main"));
        await Assert.That(sym.Kind).IsEqualTo(SymbolKind.Label);
    }

    [Test]
    public async Task ExportedConstant_PreservesKindAndIdentity()
    {
        var text = "SECTION \"A\", ROM0\nMY_VAL EQU 42\n    EXPORT MY_VAL\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("EXPORT MY_VAL");
        var sym = model.ResolveSymbol("MY_VAL", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Constant);
        await Assert.That(sym.OwnerId).IsNull();
        await Assert.That(sym.SymbolId).IsEqualTo(((string?)null, "MY_VAL"));
    }

    [Test]
    public async Task ExportedStringConstant_PreservesKindAndIdentity()
    {
        var text = "SECTION \"A\", ROM0\nMY_STR EQUS \"hello\"\n    EXPORT MY_STR\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("EXPORT MY_STR");
        var sym = model.ResolveSymbol("MY_STR", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.StringConstant);
        await Assert.That(sym.OwnerId).IsNull();
        await Assert.That(sym.SymbolId).IsEqualTo(((string?)null, "MY_STR"));
    }

    [Test]
    public async Task DuplicateExport_ProducesDiagnosticAtSecondExportDirective()
    {
        var textA = "SECTION \"A\", ROM0\nmain:\n    EXPORT main\n";
        var textB = "SECTION \"B\", ROM0\nmain:\n    EXPORT main\n";
        var treeA = SyntaxTree.Parse(SourceText.From(textA, "a.asm"));
        var treeB = SyntaxTree.Parse(SourceText.From(textB, "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        int exportStart = textB.IndexOf("EXPORT main");
        int exportEnd = exportStart + "EXPORT main".Length;

        var diag = compilation.Diagnostics.FirstOrDefault(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("already defined"));

        await Assert.That(diag).IsNotNull();
        await Assert.That(diag!.Location.Start).IsGreaterThanOrEqualTo(exportStart);
        await Assert.That(diag.Location.Start).IsLessThan(exportEnd);
    }

    [Test]
    public async Task ExportUndefinedSymbol_ProducesDiagnostic()
    {
        var text = "SECTION \"A\", ROM0\n    EXPORT missing\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));

        var compilation = Compilation.Create(tree);

        var diag = compilation.Diagnostics.FirstOrDefault(d =>
            d.Message.Contains("Cannot export undefined symbol"));

        await Assert.That(diag).IsNotNull();
    }

    [Test]
    public async Task ExportMacro_ProducesDiagnosticAtExportDirective()
    {
        var text = "SECTION \"A\", ROM0\nmy_macro: MACRO\n    nop\nENDM\n    EXPORT my_macro\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));

        var compilation = Compilation.Create(tree);

        int exportStart = text.IndexOf("EXPORT my_macro");
        int exportEnd = exportStart + "EXPORT my_macro".Length;

        var diag = compilation.Diagnostics.FirstOrDefault(d =>
            d.Message.Contains("Cannot export macro"));

        await Assert.That(diag).IsNotNull();
        await Assert.That(diag!.Location.Start).IsGreaterThanOrEqualTo(exportStart);
        await Assert.That(diag.Location.Start).IsLessThan(exportEnd);
    }

    [Test]
    public async Task ExportLocalLabel_ProducesDiagnostic()
    {
        var text = "SECTION \"A\", ROM0\nmain:\n.local:\n    EXPORT main.local\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));

        var compilation = Compilation.Create(tree);

        var diag = compilation.Diagnostics.FirstOrDefault(d =>
            d.Message.Contains("Cannot export local label"));

        await Assert.That(diag).IsNotNull();
    }

    [Test]
    public async Task DefinedMacro_AppearsInSymbolTable()
    {
        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"Main\", ROM0\nmy_macro: MACRO\n    nop\nENDM\n    my_macro\n", "main.asm"));

        var compilation = Compilation.Create(tree);
        var model = compilation.Emit();

        var sym = model.Symbols.FirstOrDefault(s => s.Name == "my_macro");

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Macro);
    }

    [Test]
    public async Task MacroSymbol_ResolvableViaSemanticModel()
    {
        var text = "SECTION \"Main\", ROM0\nmy_macro: MACRO\n    nop\nENDM\n    my_macro\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.LastIndexOf("my_macro");
        var sym = model.ResolveSymbol("my_macro", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Macro);
    }

    [Test]
    public async Task MacroAndLabel_SameName_SameOwner_ProducesDuplicate()
    {
        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"Main\", ROM0\nfoo: MACRO\n    nop\nENDM\nfoo:\n    nop\n", "main.asm"));

        var compilation = Compilation.Create(tree);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task CrossOwnerSameNameMacros_Allowed()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\ninit: MACRO\n    nop\nENDM\n    init\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\ninit: MACRO\n    halt\nENDM\n    init\n", "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SameOwner_IncludedMacro_CollidesWithRootMacro()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("macros.inc", "foo: MACRO\n    nop\nENDM\n");

        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nfoo: MACRO\n    halt\nENDM\nINCLUDE \"macros.inc\"\n", "a.asm"));

        var compilation = Compilation.Create(resolver, tree);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task IncludedSymbol_BelongsToRootOwner()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("defs.inc", "MY_CONST EQU 42\n");

        var text = "SECTION \"A\", ROM0\nINCLUDE \"defs.inc\"\n    ld a, MY_CONST\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(resolver, tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("MY_CONST", text.IndexOf("ld a"));
        var sym = model.ResolveSymbol("MY_CONST", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.OwnerId).IsEqualTo("a.asm");
    }

    [Test]
    public async Task IncludedMacro_BelongsToRootOwner()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("macros.inc", "my_macro: MACRO\n    nop\nENDM\n");

        var text = "SECTION \"A\", ROM0\nINCLUDE \"macros.inc\"\n    my_macro\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(resolver, tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.LastIndexOf("my_macro");
        var sym = model.ResolveSymbol("my_macro", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.OwnerId).IsEqualTo("a.asm");
        await Assert.That(sym.Kind).IsEqualTo(SymbolKind.Macro);
    }

    [Test]
    public async Task SameIncludeDifferentRoots_IndependentSymbols()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("defs.inc", "MY_CONST EQU 42\n");

        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nINCLUDE \"defs.inc\"\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\nINCLUDE \"defs.inc\"\n", "b.asm"));

        var compilation = Compilation.Create(resolver, treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SameOwnerIncludeCollision_ProducesDiagnostic()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("defs.inc", "MY_CONST EQU 99\n");

        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nMY_CONST EQU 42\nINCLUDE \"defs.inc\"\n", "a.asm"));

        var compilation = Compilation.Create(resolver, tree);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task ExportedFromInclude_HasExportedIdentity()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("exports.inc", "main:\n    EXPORT main\n    nop\n");

        var text = "SECTION \"A\", ROM0\nINCLUDE \"exports.inc\"\nmain_ref:\n    jp main\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(resolver, tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("jp main");
        var sym = model.ResolveSymbol("main", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Visibility).IsEqualTo(SymbolVisibility.Exported);
        await Assert.That(sym.OwnerId).IsNull();
    }

    [Test]
    public async Task GetSymbol_LocalLabelReference_ReturnsCorrectScopedSymbol()
    {
        var text =
            "SECTION \"Main\", ROM0\n" +
            "funcA:\n.done:\n    jr .done\n" +
            "funcB:\n.done:\n    jr .done\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        var labelOperands = tree.Root.DescendantNodes()
            .Where(n => n.Kind == SyntaxKind.LabelOperand)
            .ToList();

        await Assert.That(labelOperands.Count).IsGreaterThanOrEqualTo(2);

        var symA = model.GetSymbol(labelOperands[0]);
        var symB = model.GetSymbol(labelOperands[1]);

        await Assert.That(symA).IsNotNull();
        await Assert.That(symB).IsNotNull();
        await Assert.That(symA!.Name).IsEqualTo("funcA.done");
        await Assert.That(symB!.Name).IsEqualTo("funcB.done");
    }

    [Test]
    public async Task GetDeclaredSymbol_LocalLabelDeclaration_ReturnsCorrectScopedSymbol()
    {
        var text = "SECTION \"Main\", ROM0\nfuncA:\n.done:\n    nop\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        var labelNode = tree.Root.ChildNodes()
            .Where(n => n.Kind == SyntaxKind.LabelDeclaration)
            .First(n => n.ChildTokens().Any(t => t.Text == ".done"));

        var sym = model.GetDeclaredSymbol(labelNode);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Name).IsEqualTo("funcA.done");
    }

    [Test]
    public async Task GetDeclaredSymbol_EquDirective_ReturnsConstantSymbol()
    {
        var text = "SECTION \"Main\", ROM0\nMY_VAL EQU 42\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        var directive = tree.Root.ChildNodes()
            .First(n => n.Kind == SyntaxKind.SymbolDirective);

        var sym = model.GetDeclaredSymbol(directive);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Constant);
        await Assert.That(sym.Name).IsEqualTo("MY_VAL");
    }

    [Test]
    public async Task GetDeclaredSymbol_EqusDirective_ReturnsStringConstantSymbol()
    {
        var text = "SECTION \"Main\", ROM0\nMY_STR EQUS \"hello\"\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        var directive = tree.Root.ChildNodes()
            .First(n => n.Kind == SyntaxKind.SymbolDirective);

        var sym = model.GetDeclaredSymbol(directive);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.StringConstant);
        await Assert.That(sym.Name).IsEqualTo("MY_STR");
    }

    [Test]
    public async Task LookupSymbols_DoesNotExposeOtherOwnerLocals()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nsecret:\n    nop\n", "a.asm"));
        var textB = "SECTION \"B\", ROM0\n    nop\n";
        var treeB = SyntaxTree.Parse(SourceText.From(textB, "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);
        var modelB = compilation.GetSemanticModel(treeB);

        int pos = textB.IndexOf("nop");
        var visibleNames = modelB.LookupSymbols(pos)
            .Select(s => s.Name)
            .ToList();

        await Assert.That(visibleNames).DoesNotContain("secret");
    }

    [Test]
    public async Task LookupSymbols_IncludesExportedSymbols()
    {
        var text = "SECTION \"A\", ROM0\nmain:\n    EXPORT main\n    nop\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("nop");
        var visibleNames = model.LookupSymbols(pos)
            .Select(s => s.Name)
            .ToList();

        await Assert.That(visibleNames).Contains("main");
    }

    [Test]
    public async Task LookupSymbols_LocalLabelScope_OnlyShowsCurrentScope()
    {
        var text =
            "SECTION \"Main\", ROM0\n" +
            "funcA:\n.done:\n    nop\n" +
            "funcB:\n.done:\n    nop\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int posA = text.IndexOf("nop");
        var namesA = model.LookupSymbols(posA)
            .Where(s => s.Name.Contains('.'))
            .Select(s => s.Name)
            .ToList();

        int posB = text.LastIndexOf("nop");
        var namesB = model.LookupSymbols(posB)
            .Where(s => s.Name.Contains('.'))
            .Select(s => s.Name)
            .ToList();

        await Assert.That(namesA).Contains("funcA.done");
        await Assert.That(namesA).DoesNotContain("funcB.done");
        await Assert.That(namesB).Contains("funcB.done");
        await Assert.That(namesB).DoesNotContain("funcA.done");
    }

    [Test]
    public async Task NullFilePath_MultiTree_Rejected()
    {
        var treeA = SyntaxTree.Parse("SECTION \"A\", ROM0\nmain:\n    nop\n");
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\n    nop\n", "b.asm"));

        await Assert.That(() => Compilation.Create(treeA, treeB))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task SingleFile_NoChange()
    {
        var tree = SyntaxTree.Parse(SourceText.From(
            "SECTION \"Main\", ROM0\nmain:\n    jp main\n", "main.asm"));

        var compilation = Compilation.Create(tree);

        await Assert.That(compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Count()).IsEqualTo(0);
    }
}
```

* [ ] **Step 11: Normalization verification**

Confirm all are true:

* no label/constant/string constant/macro definition bypasses owner-aware APIs

* no flat global namespace remains for non-exported definitions

* no test in this plan relies on LSP, workspace, URI, or editor behavior

* no test assumes cross-owner exported binding resolution

* no implementation step depends on undocumented token ordering beyond cases explicitly covered by tests

* [ ] **Step 12: Run tests**

```bash
cd /c/projekty/koh && dotnet test
```

* [ ] **Step 13: Commit**

```bash
git add src/Koh.Core/Symbols/SymbolResolutionContext.cs src/Koh.Core/Symbols/Symbol.cs src/Koh.Core/Symbols/SymbolTable.cs src/Koh.Core/Binding/Binder.cs src/Koh.Core/Binding/AssemblyExpander.cs src/Koh.Core/SemanticModel.cs src/Koh.Core/Compilation.cs tests/Koh.Core.Tests/Symbols/SymbolOwnershipTests.cs
git commit -m "feat: add owner-aware symbol identity model with export promotion and macro registration"
```

---
