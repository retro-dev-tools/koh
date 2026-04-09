# Plan 3 — Backend Polish and Performance

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete non-LSP polish work — macro arity metadata, linker diagnostics, parallel parsing, Native AOT for CLI tools, LSP AOT feasibility assessment, and incremental document invalidation.

**Non-goals:** This plan does not implement new LSP features (rename, symbol finder, inlay hints, etc.) or redesign the ownership model. It may improve backend data consumed by existing LSP features (e.g., macro arity improves existing signature help).

**Spec:** `docs/superpowers/specs/2026-03-25-koh-assembler-design.md`
**Original plan:** `docs/superpowers/plans/2026-03-25-koh-implementation-plan.md`
**Parent index:** `docs/superpowers/plans/2026-04-02-koh-completion-plan.md`

**Recommended order:**
1. **Task 1 (Macro Arity)** — improves signature help accuracy, low risk
2. **Task 2 (Constraint Diagnostics)** — improves linker UX, independent of other tasks
3. **Task 3 (Parallel Parsing)** — performance improvement, low risk
4. **Task 4 (AOT CLI)** — ship native binaries, independent
5. **Task 5 (AOT LSP Feasibility)** — assessment only, may not produce a binary
6. **Task 6 (Incremental Invalidation)** — highest complexity, most benefit for LSP responsiveness

---

## Phase Overview

| Task | Milestone | Status | You can now... |
|------|-----------|--------|----------------|
| 1 | Macro Arity Metadata | Not done | Query macro call-site arity from the semantic pipeline |
| 2 | Constraint Diagnostics | Not done | See exactly which sections conflict and by how many bytes |
| 3 | Parallel Parsing | Not done | Multi-file projects parse concurrently on all cores |
| 4 | AOT: CLI Tools | Not done | Ship `koh-asm` and `koh-link` as single native binaries |
| 5 | AOT: LSP Feasibility | Not done | Determine whether `koh-lsp` can ship as AOT |
| 6 | Incremental Invalidation | Not done | Region-based reparse for single-region edits, safe fallback to full reparse |

---

## Task 1: Macro Call-Site Arity Metadata

**Milestone:** The binding pipeline tracks observed macro call-site arities, making them queryable from `SemanticModel` via the resolved `Symbol`.

### Exit criteria

* Arity metadata is stored in `BindingResult`, queryable from `SemanticModel` — not `EmitModel`
* Arity is keyed by `Symbol` reference (the same macro `Symbol` object that `SymbolTable.DefineMacro` returns), matching the owner-aware identity model
* The primary query API is `GetMacroArity(Symbol)`, taking a resolved macro symbol
* A convenience `GetMacroArity(string)` may exist but must delegate through symbol resolution, not bypass it
* Arity reflects the maximum observed call-site arg count across all call sites in the compilation
* The LSP's body-scanning `GetMacroArity` in `KohLanguageServer.cs` is replaced: resolve macro symbol first, then query arity
* All tests pass including edge cases for casing, nested calls, and grouping

### Background

**Critical distinction:** RGBDS macros have **no declared parameter list**. A macro can accept any number of arguments — the body uses `\1`..`\9` and `_NARG` to access them, and different call sites can pass different argument counts. What we expose is the **maximum observed call-site arity** — a best-effort hint, not a language-level signature.

**Why max-only, not observed set:** A set of `{1,3,7}` would be richer, but signature help needs a single parameter count to generate the `\1`..`\N` list. The max gives the most complete picture. If a future feature wants to show "usually called with 2 args" or highlight unusual call sites, extending to a set is straightforward. For now, max-only is the pragmatic choice.

**Current state:** The LSP has a working `GetMacroArity` in `KohLanguageServer.cs` (line 1127) that scans the macro definition body for `\N` parameter references and returns the highest N found. This is the approach the plan deliberately replaces: a macro using only `\1` but regularly called with 3 args would report arity 1 instead of 3.

### Design decisions

**Why not `EmitModel`:** `EmitModel` is frozen output consumed by the linker and `.kobj` writer. It is an assembly output model, not an editor metadata carrier. The LSP already accesses `SemanticModel` via `compilation.GetSemanticModel(tree)`, which wraps `BindingResult`. Arity metadata belongs in `BindingResult`, exposed through `SemanticModel` — not threaded through `EmitModel`.

**Why key by symbol identity, not bare name:** Macros are stored in `SymbolTable._macroSymbols` keyed by `(OwnerId, Name)` with case-insensitive name comparison. In multi-owner compilations, different owners can define macros with the same name. A bare `Dictionary<string, int>` would conflate them. The `AssemblyExpander` already looks up the macro `Symbol` via `SymbolTable` during expansion — the same `Symbol` object that was returned by `DefineMacro`. Key the arity map by that `Symbol` reference.

### Approach

During expansion, `AssemblyExpander.ExpandMacroCall` already collects arguments via `CollectMacroArgs(tokens, startIndex: 1)` and looks up the macro `Symbol` from `SymbolTable`. After collecting args, record `(macroSymbol, args.Count)` keyed by the `Symbol` reference. After binding completes, transfer the arity map into `BindingResult`. Expose through `SemanticModel` with a `Symbol`-based primary API and a name-based convenience that resolves first.

### Files

* Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`
* Modify: `src/Koh.Core/Binding/BindingResult.cs`
* Modify: `src/Koh.Core/Binding/Binder.cs`
* Modify: `src/Koh.Core/SemanticModel.cs`
* Modify: `src/Koh.Lsp/KohLanguageServer.cs` (replace body-scanning `GetMacroArity`)
* Test: `tests/Koh.Core.Tests/Binding/MacroMetadataTests.cs`

### Step checklist

- [ ] **Step 1: Write failing tests**

```csharp
using Koh.Core;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class MacroMetadataTests
{
    [Test]
    public async Task MacroArity_TwoArgCall_Reports2()
    {
        var tree = SyntaxTree.Parse(
            "SECTION \"Main\", ROM0\n" +
            "my_add: MACRO\n    ld a, \\1\n    add a, \\2\nENDM\n" +
            "    my_add b, c\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        var arity = model.GetMacroArity("my_add");
        await Assert.That(arity).IsEqualTo(2);
    }

    [Test]
    public async Task MacroArity_VaryingCallSites_ReportsMax()
    {
        var tree = SyntaxTree.Parse(
            "SECTION \"Main\", ROM0\n" +
            "flex: MACRO\n    nop\nENDM\n" +
            "    flex a\n" +
            "    flex a, b, c\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        await Assert.That(model.GetMacroArity("flex")).IsEqualTo(3);
    }

    [Test]
    public async Task MacroArity_NoArgCall_Reports0()
    {
        var tree = SyntaxTree.Parse(
            "SECTION \"Main\", ROM0\n" +
            "init: MACRO\n    xor a\nENDM\n" +
            "    init\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        await Assert.That(model.GetMacroArity("init")).IsEqualTo(0);
    }

    [Test]
    public async Task MacroArity_DefinedButNeverCalled_ReturnsNull()
    {
        var tree = SyntaxTree.Parse(
            "SECTION \"Main\", ROM0\n" +
            "unused: MACRO\n    nop\nENDM\n" +
            "    nop\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        await Assert.That(model.GetMacroArity("unused")).IsNull();
    }

    [Test]
    public async Task MacroArity_NoCalls_AllReturnsNull()
    {
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\nnop\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        await Assert.That(model.GetMacroArity("anything")).IsNull();
    }

    [Test]
    public async Task MacroArity_ParenGroupedArgs_CountsCorrectly()
    {
        var tree = SyntaxTree.Parse(
            "SECTION \"Main\", ROM0\n" +
            "my_call: MACRO\n    nop\nENDM\n" +
            "    my_call BANK(some_label), $42\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        await Assert.That(model.GetMacroArity("my_call")).IsEqualTo(2);
    }

    [Test]
    public async Task MacroArity_CaseInsensitive_ReportsCorrectly()
    {
        var tree = SyntaxTree.Parse(
            "SECTION \"Main\", ROM0\n" +
            "MyMacro: MACRO\n    nop\nENDM\n" +
            "    mymacro a, b\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        // Lookup should be case-insensitive, matching SymbolTable behavior
        await Assert.That(model.GetMacroArity("MYMACRO")).IsEqualTo(2);
        await Assert.That(model.GetMacroArity("MyMacro")).IsEqualTo(2);
    }

    [Test]
    public async Task MacroArity_NestedMacroCall_TracksOuter()
    {
        // inner is called with 1 arg from inside outer's expansion;
        // outer is called with 2 args from the top level
        var tree = SyntaxTree.Parse(
            "SECTION \"Main\", ROM0\n" +
            "inner: MACRO\n    ld a, \\1\nENDM\n" +
            "outer: MACRO\n    inner \\1\nENDM\n" +
            "    outer b, c\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        await Assert.That(model.GetMacroArity("outer")).IsEqualTo(2);
        await Assert.That(model.GetMacroArity("inner")).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Track call-site arity in AssemblyExpander**

In `AssemblyExpander`, add an arity tracker keyed by `Symbol` reference. The expander already resolves the macro `Symbol` from `SymbolTable` during `ExpandMacroCall`. After `CollectMacroArgs`, update the max:

```csharp
private readonly Dictionary<Symbol, int> _macroArities = new(ReferenceEqualityComparer.Instance);

// In ExpandMacroCall, after: var args = CollectMacroArgs(tokens, startIndex: 1);
// where macroSymbol is the Symbol already looked up from SymbolTable:
if (!_macroArities.TryGetValue(macroSymbol, out var existing) || args.Count > existing)
    _macroArities[macroSymbol] = args.Count;
```

Expose: `public IReadOnlyDictionary<Symbol, int> MacroArities => _macroArities;`

- [ ] **Step 3: Thread through BindingResult → SemanticModel**

In `BindingResult`, add:

```csharp
public IReadOnlyDictionary<Symbol, int>? MacroArities { get; }
```

Update `BindingResult` constructor. In `Binder.Bind`, pass `_expander.MacroArities`.

In `SemanticModel`, add query methods:

```csharp
/// <summary>Primary API: query arity for a resolved macro symbol.</summary>
public int? GetMacroArity(Symbol macroSymbol)
{
    if (_result.MacroArities != null &&
        _result.MacroArities.TryGetValue(macroSymbol, out var arity))
        return arity;
    return null;
}

/// <summary>Convenience: resolve macro by name first, then query arity.</summary>
public int? GetMacroArity(string macroName)
{
    var symbol = ResolveSymbol(macroName, 0);
    if (symbol?.Kind != SymbolKind.Macro)
        return null;
    return GetMacroArity(symbol);
}
```

Do NOT add `MacroArities` to `EmitModel`. The linker and `.kobj` writer have no use for it.

**Note on the name-based convenience:** `ResolveSymbol(macroName, 0)` uses position 0, which works because macros are global (not position-scoped like local labels). If macro resolution ever becomes position-sensitive, the name-based convenience would need a position parameter.

- [ ] **Step 4: Update LSP to use pipeline arity**

Replace the body-scanning `GetMacroArity` in `KohLanguageServer.cs` (and its helpers `ScanMacroParams`, `FindNextSiblingOfKind`). The LSP already resolves the macro `Symbol` for signature help — pass that resolved symbol to `semanticModel.GetMacroArity(macroSymbol)` instead of calling the name-based convenience.

- [ ] **Step 5: Run tests, verify pass**

```bash
cd /c/projekty/koh && dotnet test
```

- [ ] **Step 6: Commit**

---

## Task 2: Constraint Solver Diagnostics

**Milestone:** When section placement fails, the linker reports specific section names, sizes, bank numbers, and overflow amounts.

### Exit criteria

* Single-section-too-large errors report section name, size, and bank capacity
* Multi-section overflow errors report the failing section name and overflow amount
* Fixed-bank overflow errors report the bank number
* Fixed-address overlap errors report both section names and overlap region
* Overlap diagnostics are emitted exactly once per conflicting pair (not duplicated)
* Floating sections that fit in other banks produce no errors
* Existing linker tests still pass

### Background

**Current state:** `SectionPlacer.PlaceRegion` (line 111) has a single generic message: `"Section '{name}' ({size} bytes) does not fit in {type} memory region"`. No bank numbers, no overflow amounts, no fixed-address overlap detection.

**Key constraint:** Diagnostic messages must match the actual allocator model. The current `SectionPlacer` uses a linear end-of-bank cursor — no fragmentation, no gaps. "Largest free space" means the distance from the bank cursor to the bank end, not a fragmentation-aware measure. The error message should say "free space" (not "contiguous free space") to be accurate to the model.

**Overlap detection timing:** Fixed-address overlaps are detected AFTER placement is committed (the section's `PlacedAddress` is set). To avoid duplicate diagnostics, overlap detection runs once per fixed-address section against all previously placed sections, not bidirectionally.

### Files

* Modify: `src/Koh.Linker.Core/SectionPlacer.cs`
* Test: `tests/Koh.Linker.Tests/ConstraintDiagnosticTests.cs`

### Step checklist

- [ ] **Step 1: Write failing tests**

```csharp
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Linker.Core;

namespace Koh.Linker.Tests;

public class ConstraintDiagnosticTests
{
    [Test]
    public async Task Placement_SingleSectionTooLarge_ReportsSizeAndCapacity()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[] { CreateSection("huge", SectionType.Rom0, size: 0x5000) });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("huge");
        await Assert.That(errors[0].Message).Contains("20480");
        await Assert.That(errors[0].Message).Contains("16384");
    }

    [Test]
    public async Task Placement_TwoSectionsExceedBank_ReportsFailingSection()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("data_a", SectionType.Rom0, size: 0x3000),
            CreateSection("data_b", SectionType.Rom0, size: 0x2000),
        });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("data_b");
    }

    [Test]
    public async Task Placement_FixedBankOverflow_ReportsBankNumber()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("code_a", SectionType.RomX, size: 0x3800, bank: 1),
            CreateSection("code_b", SectionType.RomX, size: 0x1000, bank: 1),
        });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("bank 1");
    }

    [Test]
    public async Task Placement_OverflowAmount_Reported()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        // ROM0 capacity = 0x4000 (16384 bytes)
        // A = 0x2000, B = 0x1800 → used = 0x3800, remaining = 0x800 (2048)
        // C = 0x1000 (4096) needs 4096 but only 2048 free → overflow = 2048
        placer.PlaceAll(new[]
        {
            CreateSection("A", SectionType.Rom0, size: 0x2000),
            CreateSection("B", SectionType.Rom0, size: 0x1800),
            CreateSection("C", SectionType.Rom0, size: 0x1000),
        });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("C");
        await Assert.That(errors[0].Message).Contains("4096"); // section size
        await Assert.That(errors[0].Message).Contains("2048"); // overflow amount
    }

    [Test]
    public async Task Placement_FixedAddressOverlap_ReportsBothSections()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("vectors", SectionType.Rom0, size: 0x100, fixedAddress: 0x0000),
            CreateSection("overlap", SectionType.Rom0, size: 0x100, fixedAddress: 0x0080),
        });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("overlap");
        await Assert.That(errors[0].Message).Contains("vectors");
    }

    [Test]
    public async Task Placement_FixedAddressOverlap_EmittedOnceNotTwice()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("first", SectionType.Rom0, size: 0x100, fixedAddress: 0x0000),
            CreateSection("second", SectionType.Rom0, size: 0x100, fixedAddress: 0x0080),
        });

        // Only one overlap diagnostic, not two mirrored ones
        var overlapErrors = diags.ToList()
            .Where(d => d.Message.Contains("overlaps"))
            .ToList();
        await Assert.That(overlapErrors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Placement_FloatingFitsElsewhere_NoError()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("A", SectionType.RomX, size: 0x3000),
            CreateSection("B", SectionType.RomX, size: 0x3000),
        });

        await Assert.That(diags.ToList().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Placement_NoRoomAnywhere_ReportsLargestFreeSpace()
    {
        // Use ROM0 (single bank, 0x4000 capacity) for simplicity — no bank-count assumptions
        // Fill most of ROM0, then try a floating section that won't fit in remaining space
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            // 0x3800 fills most of ROM0's 0x4000 capacity, leaving 0x800 (2048) free
            CreateSection("fill", SectionType.Rom0, size: 0x3800),
            // 0x1000 (4096) won't fit in remaining 2048
            CreateSection("toobig", SectionType.Rom0, size: 0x1000),
        });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("toobig");
        await Assert.That(errors[0].Message).Contains("free space");
    }

    private static LinkerSection CreateSection(string name, SectionType type,
        int size, int? fixedAddress = null, int? bank = null)
    {
        var data = new SectionData(name, type, fixedAddress, bank,
            new byte[size], Array.Empty<PatchEntry>());
        return new LinkerSection(data, "test.asm");
    }
}
```

- [ ] **Step 2: Add overlap detection for fixed-address sections**

In `SectionPlacer.PlaceRegion`, after committing a fixed-address placement, check for overlaps against all previously placed sections in the same bank. Emit one diagnostic per overlapping pair, from the perspective of the later-placed section only:

```csharp
// After setting PlacedAddress/PlacedBank for a fixed-address section:
int secStart = section.PlacedAddress;
int secEnd = secStart + section.Data.Length;
foreach (var other in sections)
{
    if (other == section || other.PlacedAddress < 0 || other.PlacedBank != bank)
        continue;
    int otherEnd = other.PlacedAddress + other.Data.Length;
    if (secStart < otherEnd && secEnd > other.PlacedAddress)
    {
        int overlapBytes = Math.Min(secEnd, otherEnd) - Math.Max(secStart, other.PlacedAddress);
        _diagnostics.Report(default,
            $"Section '{section.Name}' (${secStart:X4}-${secEnd - 1:X4}) overlaps with " +
            $"'{other.Name}' (${other.PlacedAddress:X4}-${otherEnd - 1:X4}) in bank {bank} " +
            $"by {overlapBytes} bytes");
    }
}
```

- [ ] **Step 3: Improve floating-section error messages**

Replace the generic `"does not fit in {Type} memory region"` (line 111-113) with context-aware messages that distinguish three failure cases: section exceeds bank capacity, fixed-bank overflow with usage details, and no-bank-available with largest free space.

- [ ] **Step 4: Run tests, verify pass**

```bash
cd /c/projekty/koh && dotnet test
```

- [ ] **Step 5: Commit**

---

## Task 3: Parallel Parsing

**Milestone:** `Compilation.CreateFromSources()` parses multiple files concurrently.

### Exit criteria

* `CreateFromSources` produces identical trees/diagnostics to sequential parsing
* File order is preserved in the output regardless of thread scheduling
* Repeated calls with the same input produce identical results (determinism)
* No shared mutable state is introduced

### Background

**Thread safety evidence:** The parse pipeline (`SyntaxTree.Parse` → `Parser` → `Lexer`) was audited for shared mutable state. No shared mutable state was found:

* `Lexer` has one static field: `Keywords`, a `static readonly Dictionary` populated at class init and never mutated.
* `Parser` has zero static mutable fields. All static members are pure helper predicates.
* `SyntaxTree.Parse` creates a fresh `Parser` (which creates a fresh `Lexer`) per call.
* `SourceText.From` has no caching or interning — every call produces a new instance.
* `GreenToken`, `GreenNode`, `GreenTrivia` are all immutable value types or sealed immutable classes.

Based on this audit, parallel parsing is expected to be safe. The determinism and sequential-equivalence tests in this task validate that assumption empirically. If a future change introduces shared state, the determinism test should catch it as nondeterminism.

### Files

* Modify: `src/Koh.Core/Compilation.cs`
* Test: `tests/Koh.Core.Tests/ParallelParseTests.cs`

### Step checklist

- [ ] **Step 1: Write failing tests**

```csharp
using Koh.Core;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests;

public class ParallelParseTests
{
    [Test]
    public async Task CreateFromSources_ParsesAllFiles()
    {
        var sources = new[]
        {
            SourceText.From("SECTION \"A\", ROM0\nnop\n", "a.asm"),
            SourceText.From("SECTION \"B\", ROMX\nhalt\n", "b.asm"),
            SourceText.From("SECTION \"C\", ROMX\nstop\n", "c.asm"),
        };

        var compilation = Compilation.CreateFromSources(sources);

        await Assert.That(compilation.SyntaxTrees.Count).IsEqualTo(3);
        foreach (var tree in compilation.SyntaxTrees)
        {
            await Assert.That(tree.Root).IsNotNull();
            await Assert.That(tree.Diagnostics.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task CreateFromSources_PreservesFileOrder()
    {
        var sources = new[]
        {
            SourceText.From("nop\n", "first.asm"),
            SourceText.From("halt\n", "second.asm"),
            SourceText.From("stop\n", "third.asm"),
        };

        var compilation = Compilation.CreateFromSources(sources);

        await Assert.That(compilation.SyntaxTrees[0].Text.FilePath).IsEqualTo("first.asm");
        await Assert.That(compilation.SyntaxTrees[1].Text.FilePath).IsEqualTo("second.asm");
        await Assert.That(compilation.SyntaxTrees[2].Text.FilePath).IsEqualTo("third.asm");
    }

    [Test]
    public async Task CreateFromSources_ParserErrorsPerFile()
    {
        var sources = new[]
        {
            SourceText.From("nop\n", "good.asm"),
            SourceText.From("ld a,\n", "bad.asm"),
        };

        var compilation = Compilation.CreateFromSources(sources);

        await Assert.That(compilation.SyntaxTrees[0].Diagnostics.Count).IsEqualTo(0);
        await Assert.That(compilation.SyntaxTrees[1].Diagnostics.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task CreateFromSources_ManyFiles()
    {
        var sources = new SourceText[50];
        for (int i = 0; i < 50; i++)
            sources[i] = SourceText.From(
                $"SECTION \"S{i}\", ROMX\nlabel_{i}:\n    ld a, {i % 256}\n",
                $"file_{i}.asm");

        var compilation = Compilation.CreateFromSources(sources);
        await Assert.That(compilation.SyntaxTrees.Count).IsEqualTo(50);
    }

    [Test]
    public async Task CreateFromSources_Deterministic_RepeatedCallsProduceIdenticalTrees()
    {
        var sources = new SourceText[20];
        for (int i = 0; i < 20; i++)
            sources[i] = SourceText.From(
                $"SECTION \"S{i}\", ROMX\nlbl_{i}:\n.local:\n    ld a, {i}\n    jr .local\n",
                $"file_{i}.asm");

        // Parse 5 times, compare all results against the first
        var baseline = Compilation.CreateFromSources(sources);
        for (int run = 0; run < 5; run++)
        {
            var result = Compilation.CreateFromSources(sources);
            await Assert.That(result.SyntaxTrees.Count).IsEqualTo(baseline.SyntaxTrees.Count);
            for (int i = 0; i < baseline.SyntaxTrees.Count; i++)
            {
                var bTokens = CollectTokenTexts(baseline.SyntaxTrees[i].Root);
                var rTokens = CollectTokenTexts(result.SyntaxTrees[i].Root);
                await Assert.That(rTokens).IsEqualTo(bTokens);

                await Assert.That(result.SyntaxTrees[i].Diagnostics.Count)
                    .IsEqualTo(baseline.SyntaxTrees[i].Diagnostics.Count);
            }
        }
    }

    [Test]
    public async Task CreateFromSources_MatchesSequentialParse()
    {
        var sources = new[]
        {
            SourceText.From("SECTION \"A\", ROM0\nnop\n", "a.asm"),
            SourceText.From("ld a,\n", "bad.asm"), // intentional parse error
            SourceText.From("SECTION \"B\", ROMX\nhalt\n", "b.asm"),
        };

        var parallel = Compilation.CreateFromSources(sources);
        var sequential = Compilation.Create(
            sources.Select(s => SyntaxTree.Parse(s)).ToArray());

        for (int i = 0; i < sources.Length; i++)
        {
            var pTokens = CollectTokenTexts(parallel.SyntaxTrees[i].Root);
            var sTokens = CollectTokenTexts(sequential.SyntaxTrees[i].Root);
            await Assert.That(pTokens).IsEqualTo(sTokens);

            await Assert.That(parallel.SyntaxTrees[i].Diagnostics.Count)
                .IsEqualTo(sequential.SyntaxTrees[i].Diagnostics.Count);
        }
    }

    private static List<string> CollectTokenTexts(SyntaxNode node)
    {
        var texts = new List<string>();
        CollectWalk(node, texts);
        return texts;
    }

    private static void CollectWalk(SyntaxNode node, List<string> texts)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsToken && child.AsToken!.Kind != SyntaxKind.EndOfFileToken)
                texts.Add(child.AsToken!.Text);
            else if (child.IsNode)
                CollectWalk(child.AsNode!, texts);
        }
    }
}
```

- [ ] **Step 2: Implement `Compilation.CreateFromSources`**

```csharp
public static Compilation CreateFromSources(IReadOnlyList<SourceText> sources,
    BinderOptions options = default, TextWriter? printOutput = null)
{
    var trees = new SyntaxTree[sources.Count];
    Parallel.For(0, sources.Count, i => { trees[i] = SyntaxTree.Parse(sources[i]); });
    return new Compilation(trees, printOutput, options);
}
```

No threshold is introduced in this task. If very small compilations show regressions in measurement later, thresholding can be added separately.

- [ ] **Step 3: Run tests, verify pass**

```bash
cd /c/projekty/koh && dotnet test
```

- [ ] **Step 4: Commit**

---

## Task 4: Native AOT for CLI Tools

**Milestone:** `koh-asm` and `koh-link` publish as single native binaries that produce identical output to the managed build.

### Exit criteria

* Both CLI projects publish with `<PublishAot>true</PublishAot>`
* Zero unreviewed AOT/trimming warnings during publish (warnings-as-errors or explicit suppression with justification)
* Published binaries produce a `.kobj` / `.gb` that is byte-identical to the managed-build output on the same input
* Process exits with code 0 on success, non-zero on error
* Self-contained (no .NET runtime dependency)

### Files

* Modify: `src/Koh.Asm/Koh.Asm.csproj`
* Modify: `src/Koh.Link/Koh.Link.csproj`
* Create: `src/Koh.Asm/Properties/PublishProfiles/win-x64-aot.pubxml`
* Create: `src/Koh.Link/Properties/PublishProfiles/win-x64-aot.pubxml`

### Step checklist

- [ ] **Step 1: Enable AOT in both CLI csprojs**

Add to both `Koh.Asm.csproj` and `Koh.Link.csproj`:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

`InvariantGlobalization` eliminates ICU dependency. Assembler tools are unlikely to depend on locale-sensitive behavior, but verify during the smoke test that diagnostic messages, file path handling, and numeric formatting still produce correct output. If any locale-dependent behavior is discovered, remove `InvariantGlobalization` rather than working around it.

- [ ] **Step 2: Publish and resolve all warnings**

```bash
cd /c/projekty/koh
dotnet publish src/Koh.Asm -c Release -r win-x64 -o publish/win-x64/asm --no-restore 2>&1
dotnet publish src/Koh.Link -c Release -r win-x64 -o publish/win-x64/link --no-restore 2>&1
```

**Warning policy:** Zero unreviewed warnings. For each warning:

* If the warning identifies dead reflection or trimming-unsafe code, fix the code
* If the warning is a false positive on code that is provably safe, suppress with `[UnconditionalSuppressMessage]` and a comment explaining why
* Do NOT use blanket `<SuppressTrimAnalysisWarnings>` or `<TrimmerSingleWarn>`

- [ ] **Step 3: Verify correctness — byte-identical output (Git Bash)**

```bash
cd /c/projekty/koh

# Assemble and link with managed build
dotnet run --project src/Koh.Asm -- tests/fixtures/simple.asm -o test-managed.kobj
dotnet run --project src/Koh.Link -- test-managed.kobj -o test-managed.gb

# Assemble and link with AOT build
publish/win-x64/asm/koh-asm.exe tests/fixtures/simple.asm -o test-aot.kobj
publish/win-x64/link/koh-link.exe test-aot.kobj -o test-aot.gb

# Compare outputs
diff test-managed.kobj test-aot.kobj
diff test-managed.gb test-aot.gb

# Verify exit codes
echo $?
```

If no fixture file exists at `tests/fixtures/simple.asm`, create a minimal one:

```asm
SECTION "Main", ROM0
main:
    ld a, $42
    halt
```

Both `.kobj` and `.gb` must be byte-identical between managed and AOT builds. If they differ, investigate — do not treat "non-empty file" as sufficient.

- [ ] **Step 4: Add publish profiles**

Create `src/Koh.Asm/Properties/PublishProfiles/win-x64-aot.pubxml`:

```xml
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishAot>true</PublishAot>
    <SelfContained>true</SelfContained>
    <InvariantGlobalization>true</InvariantGlobalization>
    <StripSymbols>true</StripSymbols>
  </PropertyGroup>
</Project>
```

Same for `src/Koh.Link/Properties/PublishProfiles/win-x64-aot.pubxml`.

- [ ] **Step 5: Run full test suite against managed build to confirm no regressions**

```bash
cd /c/projekty/koh && dotnet test
```

- [ ] **Step 6: Commit**

---

## Task 5: Native AOT Feasibility for LSP

**Milestone:** Determine whether `koh-lsp` can be published as a Native AOT binary. Deliverable: a written decision document with evidence, regardless of outcome.

### Exit criteria

* An AOT publish is attempted with `<PublishAot>true</PublishAot>` explicitly set
* If it compiles: a defined smoke test passes
* If it fails: warnings/errors are cataloged with root causes identified
* A decision document is written in both success and failure cases

### Smoke test checklist (if AOT compiles)

Launch the AOT binary, connect VS Code, and verify each:

1. `initialize` / `initialized` handshake completes
2. Open a `.asm` document — `textDocument/didOpen` accepted
3. Diagnostics appear for a file with a known error
4. Hover on a label returns symbol info
5. Go-to-definition works for a label reference
6. Semantic tokens render (the LSP already implements `textDocument/semanticTokens/full`)
7. `shutdown` / `exit` completes without crash

If any fail, document which and why.

### Step checklist

- [ ] **Step 1: Attempt AOT publish with explicit opt-in**

Temporarily add `<PublishAot>true</PublishAot>` to `Koh.Lsp.csproj` (do not commit yet), then publish:

```bash
cd /c/projekty/koh
dotnet publish src/Koh.Lsp -c Release -r win-x64 -o publish/win-x64/lsp 2>&1 | tee lsp-aot-log.txt
```

- [ ] **Step 2: Assess result**

**If it compiles without errors:** Run the smoke test checklist above. If all pass, the binary is viable.

**If it fails or has warnings:** Catalog every warning/error from `lsp-aot-log.txt`. Common blockers in LSP stacks include reflection-heavy JSON serialization and RPC dispatch — identify the actual blockers from the publish output rather than assuming them.

For each blocker, identify:

* Root cause (which dependency, which API)
* Whether a fix exists (source generators, alternative transport, `[DynamicDependency]`)
* Estimated effort to fix

- [ ] **Step 3: Write decision document**

Create `docs/decisions/lsp-aot-decision.md` documenting:

* Runtime identifier tested
* Build outcome (success/failure)
* Warnings encountered (even if build succeeded)
* Smoke test results (if applicable)
* Blockers and root causes (if applicable)
* Recommendation: enable AOT / defer with estimated fix effort / accept managed-only LSP
* Date of assessment

- [ ] **Step 4: Commit**

```bash
# If AOT works and smoke test passes:
git add src/Koh.Lsp/Koh.Lsp.csproj docs/decisions/lsp-aot-decision.md
git commit -m "feat: enable Native AOT for koh-lsp"

# If not feasible:
git add docs/decisions/lsp-aot-decision.md
git commit -m "docs: document LSP AOT assessment — [outcome summary]"
```

Clean up `lsp-aot-log.txt` — do not commit build logs.

---

## Task 6: Incremental Document Invalidation

**Milestone:** Region-based reparse attempted for single-region edits, with safe fallback to full reparse. Correctness is proven by canonical equivalence with full reparse.

### Exit criteria

* Incremental reparse produces trees that are **canonically equivalent** to full reparse (see equivalence definition below)
* `ComputeChange` is deterministic and handles ambiguous diffs correctly
* Fallback to full reparse is safe and produces correct results in all edge cases
* Trivia attachment is preserved correctly around edit boundaries
* No performance claims are made without measurement (this task proves correctness only)

### Canonical tree equivalence

Two `SyntaxTree` instances are canonically equivalent iff ALL of the following match:

1. **Token sequence:** same token kinds in the same order
2. **Token texts:** same text for each token
3. **Token spans:** same `Span.Start` and `Span.Length` for each token
4. **Token full spans:** same `FullSpan.Start` and `FullSpan.Length` for each token (covers trivia width)
5. **Leading trivia:** same trivia kinds, texts, and positions for each token's `LeadingTrivia`
6. **Trailing trivia:** same trivia kinds, texts, and positions for each token's `TrailingTrivia`
7. **Node kinds:** same `SyntaxKind` for each node in pre-order traversal
8. **Node positions:** same `Position` for each node
9. **Diagnostics:** same count, and for each diagnostic: same `Span.Start`, `Span.Length`, `Message`, and `Severity`

The test helper `AssertTreesCanonicallyEqual` must verify all nine properties. Partial checks (e.g., token texts only) are insufficient — structurally wrong trees can still pass text-only comparisons.

### `ComputeChange` specification

`ComputeChange(oldText, newText)` derives a single contiguous `TextChange` representing the minimal edit between two full-text snapshots. This is needed because `Workspace.ChangeDocument` receives the full new text, not the edit.

**Algorithm:**

1. Find the longest common prefix length `prefixLen` by comparing characters from the start
2. Find the longest common suffix length `suffixLen` by comparing characters from the end, but do not overlap with the prefix: `suffixLen = min(suffixLen, min(oldLen, newLen) - prefixLen)`
3. The changed span in old text is `[prefixLen, oldLen - suffixLen)`
4. The replacement text is `newText[prefixLen .. newLen - suffixLen]`
5. If `prefixLen + suffixLen >= oldLen` and `prefixLen + suffixLen >= newLen`, texts are identical — return null (no change)

**Ambiguity handling:** For repeated substrings (e.g., `"nop\nnop\n"` → `"halt\nnop\n"`), the algorithm deterministically attributes the change to the earliest differing position. This is correct because the incremental parser re-parses from the change start through the affected region — the exact change boundaries matter less than covering the affected statements.

**Fallback to full reparse when:**

* `ComputeChange` returns null (texts identical — no reparse needed)
* The change spans all top-level children (whole-file edit)
* No top-level children overlap the change region
* The stitched tree's total width doesn't match the new text length (safety invariant)
* Reparse start or end would be out of bounds

### Explicit limitations

* Only reuses at top-level statement granularity (children of `CompilationUnit`)
* `ComputeChange` always produces a single contiguous replacement from two full-text snapshots. Multiple separated edits between snapshots are conservatively collapsed into one broader replacement region covering everything between the first and last difference. This is correct but less precise — more statements may be reparsed than strictly necessary.
* Does NOT guarantee green node reuse as a stable API contract — it is a correctness-preserving optimization
* Performance improvement is expected but not measured or asserted by tests in this task

### Files

* Modify: `src/Koh.Core/Syntax/SyntaxTree.cs`
* Create: `src/Koh.Core/Syntax/IncrementalParser.cs`
* Modify: `src/Koh.Lsp/Workspace.cs`
* Test: `tests/Koh.Core.Tests/Syntax/IncrementalParseTests.cs`

### Step checklist

- [ ] **Step 1: Write canonical equivalence helper and failing tests**

The helper must check all nine equivalence properties:

```csharp
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests.Syntax;

public class IncrementalParseTests
{
    // --- Canonical equivalence helper ---

    private static async Task AssertTreesCanonicallyEqual(SyntaxTree a, SyntaxTree b)
    {
        var aTokens = CollectAllTokens(a.Root);
        var bTokens = CollectAllTokens(b.Root);
        await Assert.That(aTokens.Count).IsEqualTo(bTokens.Count);

        for (int i = 0; i < aTokens.Count; i++)
        {
            var at = aTokens[i];
            var bt = bTokens[i];

            // Token kind, text, span, full span
            await Assert.That(at.Kind).IsEqualTo(bt.Kind);
            await Assert.That(at.Text).IsEqualTo(bt.Text);
            await Assert.That(at.Span.Start).IsEqualTo(bt.Span.Start);
            await Assert.That(at.Span.Length).IsEqualTo(bt.Span.Length);
            await Assert.That(at.FullSpan.Start).IsEqualTo(bt.FullSpan.Start);
            await Assert.That(at.FullSpan.Length).IsEqualTo(bt.FullSpan.Length);

            // Leading trivia
            var aLeading = at.LeadingTrivia.ToList();
            var bLeading = bt.LeadingTrivia.ToList();
            await Assert.That(aLeading.Count).IsEqualTo(bLeading.Count);
            for (int t = 0; t < aLeading.Count; t++)
            {
                await Assert.That(aLeading[t].Kind).IsEqualTo(bLeading[t].Kind);
                await Assert.That(aLeading[t].Text).IsEqualTo(bLeading[t].Text);
                await Assert.That(aLeading[t].Position).IsEqualTo(bLeading[t].Position);
            }

            // Trailing trivia
            var aTrailing = at.TrailingTrivia.ToList();
            var bTrailing = bt.TrailingTrivia.ToList();
            await Assert.That(aTrailing.Count).IsEqualTo(bTrailing.Count);
            for (int t = 0; t < aTrailing.Count; t++)
            {
                await Assert.That(aTrailing[t].Kind).IsEqualTo(bTrailing[t].Kind);
                await Assert.That(aTrailing[t].Text).IsEqualTo(bTrailing[t].Text);
                await Assert.That(aTrailing[t].Position).IsEqualTo(bTrailing[t].Position);
            }
        }

        // Node kinds and positions
        var aNodes = CollectNodes(a.Root);
        var bNodes = CollectNodes(b.Root);
        await Assert.That(aNodes.Count).IsEqualTo(bNodes.Count);
        for (int i = 0; i < aNodes.Count; i++)
        {
            await Assert.That(aNodes[i].Kind).IsEqualTo(bNodes[i].Kind);
            await Assert.That(aNodes[i].Position).IsEqualTo(bNodes[i].Position);
        }

        // Diagnostics
        await Assert.That(a.Diagnostics.Count).IsEqualTo(b.Diagnostics.Count);
        for (int i = 0; i < a.Diagnostics.Count; i++)
        {
            await Assert.That(a.Diagnostics[i].Span.Start).IsEqualTo(b.Diagnostics[i].Span.Start);
            await Assert.That(a.Diagnostics[i].Span.Length).IsEqualTo(b.Diagnostics[i].Span.Length);
            await Assert.That(a.Diagnostics[i].Message).IsEqualTo(b.Diagnostics[i].Message);
            await Assert.That(a.Diagnostics[i].Severity).IsEqualTo(b.Diagnostics[i].Severity);
        }
    }

    private static List<SyntaxToken> CollectAllTokens(SyntaxNode node)
    {
        var tokens = new List<SyntaxToken>();
        CollectTokensWalk(node, tokens);
        return tokens;
    }

    private static void CollectTokensWalk(SyntaxNode node, List<SyntaxToken> tokens)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsToken && child.AsToken!.Kind != SyntaxKind.EndOfFileToken)
                tokens.Add(child.AsToken!);
            else if (child.IsNode)
                CollectTokensWalk(child.AsNode!, tokens);
        }
    }

    private static List<SyntaxNode> CollectNodes(SyntaxNode node)
    {
        var nodes = new List<SyntaxNode>();
        CollectNodesWalk(node, nodes);
        return nodes;
    }

    private static void CollectNodesWalk(SyntaxNode node, List<SyntaxNode> nodes)
    {
        nodes.Add(node);
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
                CollectNodesWalk(child.AsNode!, nodes);
        }
    }

    // --- Incremental parse helper ---

    private static (SyntaxTree incremental, SyntaxTree full) ApplyEdit(
        string originalText, TextSpan editSpan, string replacement)
    {
        var source = SourceText.From(originalText);
        var tree = SyntaxTree.Parse(source);
        var edit = new TextChange(editSpan, replacement);
        var newSource = source.WithChanges(edit);
        return (tree.WithChanges(edit, newSource), SyntaxTree.Parse(newSource));
    }

    // --- Core correctness tests ---

    [Test]
    public async Task IncrementalParse_EditMiddle_MatchesFull()
    {
        var (inc, full) = ApplyEdit("nop\nhalt\nstop\n",
            new TextSpan(4, 4), "ld a, b");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task IncrementalParse_InsertLine_MatchesFull()
    {
        var (inc, full) = ApplyEdit("nop\nstop\n",
            new TextSpan(4, 0), "halt\n");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task IncrementalParse_DeleteLine_MatchesFull()
    {
        var (inc, full) = ApplyEdit("nop\nhalt\nstop\n",
            new TextSpan(4, 5), "");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task IncrementalParse_EditFirstLine_MatchesFull()
    {
        var (inc, full) = ApplyEdit("nop\nhalt\nstop\n",
            new TextSpan(0, 3), "ld a, b");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task IncrementalParse_EditLastLine_MatchesFull()
    {
        var (inc, full) = ApplyEdit("nop\nhalt\nstop\n",
            new TextSpan(9, 4), "di\n");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task IncrementalParse_ReplaceAll_FallsBackCorrectly()
    {
        var source = SourceText.From("nop\nhalt\n");
        var tree = SyntaxTree.Parse(source);
        var edit = new TextChange(new TextSpan(0, source.Length), "stop\ndi\n");
        var newSource = source.WithChanges(edit);
        var inc = tree.WithChanges(edit, newSource);
        var full = SyntaxTree.Parse(newSource);
        await AssertTreesCanonicallyEqual(inc, full);
    }

    // --- Error recovery ---

    [Test]
    public async Task IncrementalParse_IntroduceError_MatchesDiagnostics()
    {
        var (inc, full) = ApplyEdit("nop\nhalt\nstop\n",
            new TextSpan(4, 4), "ld a,");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task IncrementalParse_FixError_MatchesDiagnostics()
    {
        var (inc, full) = ApplyEdit("nop\nld a,\nstop\n",
            new TextSpan(4, 4), "halt");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    // --- Trivia preservation ---

    [Test]
    public async Task IncrementalParse_EditNearComment_PreservesTrivia()
    {
        var text = "; header comment\nnop\nhalt ; inline\nstop\n";
        int editStart = text.IndexOf("halt ; inline");
        int editLen = "halt ; inline".Length;
        var (inc, full) = ApplyEdit(text,
            new TextSpan(editStart, editLen), "ld a, b");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task IncrementalParse_EditBeforeComment_PreservesTrailingTrivia()
    {
        var text = "nop ; keep this\nhalt\n";
        var (inc, full) = ApplyEdit(text,
            new TextSpan(0, 3), "stop");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task IncrementalParse_EditAfterComment_PreservesLeadingTrivia()
    {
        var text = "; comment\nnop\nhalt\n";
        var (inc, full) = ApplyEdit(text,
            new TextSpan(text.IndexOf("nop"), 3), "stop");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task IncrementalParse_InsertLineBeforeComment_PreservesTrivia()
    {
        var text = "nop\n; important comment\nhalt\n";
        var (inc, full) = ApplyEdit(text,
            new TextSpan(4, 0), "stop\n");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    // --- ComputeChange ambiguity ---

    [Test]
    public async Task IncrementalParse_RepeatedSubstring_Correct()
    {
        // Both lines identical — ambiguous prefix/suffix
        var (inc, full) = ApplyEdit("nop\nnop\n",
            new TextSpan(0, 3), "halt");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task IncrementalParse_RepeatedLines_EditSecond()
    {
        // Edit the second of two identical lines
        var text = "nop\nnop\nstop\n";
        var (inc, full) = ApplyEdit(text,
            new TextSpan(4, 3), "halt");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task ComputeChange_IdenticalText_ReturnsNull()
    {
        // Direct test of ComputeChange — identical texts must return null
        var source = SourceText.From("nop\nhalt\n");
        var same = SourceText.From("nop\nhalt\n");
        var change = SyntaxTree.ComputeChange(source, same);
        await Assert.That(change).IsNull();
    }

    [Test]
    public async Task ComputeChange_SingleCharDiff_ReturnsMinimalChange()
    {
        var old = SourceText.From("nop\nhalt\n");
        var changed = SourceText.From("nop\nhalt\ndi\n");
        var change = SyntaxTree.ComputeChange(old, changed);
        await Assert.That(change).IsNotNull();
        // Change should start at end of old text and insert "di\n"
        await Assert.That(change!.Value.Span.Start).IsEqualTo(old.Length);
        await Assert.That(change.Value.NewText).IsEqualTo("di\n");
    }

    [Test]
    public async Task IncrementalParse_NoOpEdit_MatchesFull()
    {
        // WithChanges with a zero-width edit should produce equivalent tree
        var source = SourceText.From("nop\nhalt\n");
        var tree = SyntaxTree.Parse(source);
        var edit = new TextChange(new TextSpan(0, 0), "");
        var inc = tree.WithChanges(edit, source);
        var full = SyntaxTree.Parse(source);
        await AssertTreesCanonicallyEqual(inc, full);
    }

    // --- Label/section boundary cases ---

    [Test]
    public async Task IncrementalParse_EditInsideLabel_MatchesFull()
    {
        var text = "SECTION \"Main\", ROM0\nmain:\n    nop\n    halt\n";
        var (inc, full) = ApplyEdit(text,
            new TextSpan(text.IndexOf("nop"), 3), "stop");
        await AssertTreesCanonicallyEqual(inc, full);
    }

    [Test]
    public async Task IncrementalParse_AddNewSection_MatchesFull()
    {
        var text = "SECTION \"A\", ROM0\nnop\n";
        var (inc, full) = ApplyEdit(text,
            new TextSpan(text.Length, 0), "SECTION \"B\", ROMX\nhalt\n");
        await AssertTreesCanonicallyEqual(inc, full);
    }
}
```

- [ ] **Step 2: Implement `ComputeChange`**

Implement as a static method (e.g., on `SyntaxTree` or a helper class):

```csharp
internal static TextChange? ComputeChange(SourceText oldText, SourceText newText)
{
    string oldStr = oldText.ToString();
    string newStr = newText.ToString();

    int prefixLen = 0;
    int minLen = Math.Min(oldStr.Length, newStr.Length);
    while (prefixLen < minLen && oldStr[prefixLen] == newStr[prefixLen])
        prefixLen++;

    int suffixLen = 0;
    int maxSuffix = minLen - prefixLen;
    while (suffixLen < maxSuffix &&
           oldStr[oldStr.Length - 1 - suffixLen] == newStr[newStr.Length - 1 - suffixLen])
        suffixLen++;

    int oldChangeLen = oldStr.Length - prefixLen - suffixLen;
    int newChangeLen = newStr.Length - prefixLen - suffixLen;

    if (oldChangeLen == 0 && newChangeLen == 0)
        return null; // identical

    return new TextChange(
        new TextSpan(prefixLen, oldChangeLen),
        newStr.Substring(prefixLen, newChangeLen));
}
```

- [ ] **Step 3: Implement `IncrementalParser`**

The incremental parser takes the old tree, the `TextChange`, and the new `SourceText`. Algorithm:

1. **Find affected range:** Walk top-level children (direct children of `CompilationUnit`). A child overlaps the edit if its `FullSpan` intersects the change span. Use `FullSpan` (not `Span`) so that leading/trailing trivia around affected statements is included in the reparse region. This prevents trivia reattachment errors at boundaries.

2. **Determine reparse boundaries:** Let `firstAffected` and `lastAffected` be the first and last overlapping children. The reparse region in the new text starts at `firstAffected.FullSpan.Start` and extends to cover the equivalent end position adjusted for the change delta (`lastAffected.FullSpan.End + changeDelta` where `changeDelta = newText.Length - change.Span.Length`).

3. **Re-parse the affected region:** Parse the substring of the new text covering the reparse region. This produces a sequence of new top-level statements.

4. **Stitch:** Build the new `CompilationUnit` by concatenating: children before `firstAffected` (positions unchanged), reparsed children (positions already correct from the new text), and children after `lastAffected` (positions shifted by `changeDelta`). Shifting positions requires rebuilding `SyntaxNode` wrappers with updated offsets.

5. **Validate:** The stitched tree's total width (sum of all children's `FullSpan.Length`) must equal `newText.Length`. If not, fall back to full reparse.

**Fallback conditions** (any triggers full reparse):

* No top-level children overlap the change
* The change spans all top-level children (reparse would cover everything anyway)
* Stitched tree total width != new text length (safety invariant)
* Reparse start or end out of bounds
* The reparsed region produces zero children (should not happen for valid input, but safety check)

- [ ] **Step 4: Add `WithChanges(TextChange, SourceText)` to `SyntaxTree`**

Public API that delegates to `IncrementalParser`. Falls back to `SyntaxTree.Parse(newSource)` on any failure.

- [ ] **Step 5: Wire into Workspace**

In `Workspace.ChangeDocument`, when receiving full new text:

1. Call `ComputeChange(oldSource, newSource)` to derive the edit
2. If null (identical), skip reparse
3. If non-null, call `oldTree.WithChanges(change, newSource)`
4. Rebind as before

- [ ] **Step 6: Run tests, verify pass**

ALL tests pass — incremental and full produce canonically equivalent trees across all nine equivalence properties.

```bash
cd /c/projekty/koh && dotnet test
```

- [ ] **Step 7: Commit**
