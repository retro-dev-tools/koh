# Plan 3 — Backend Polish and Performance

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete non-LSP polish work — macro arity metadata, linker diagnostics, parallel parsing, Native AOT for CLI tools, LSP AOT feasibility assessment, and incremental document invalidation.

**Non-goals:** This plan does NOT include rename, symbol finder, semantic tokens, inlay hints, signature help, or ownership model redesign.

**Note:** Task 1 (Macro Arity Metadata) is a prerequisite for Plan 2 Task 5 (Signature Help). It should be completed before or in parallel with Plan 2 Phase B.

**Spec:** `docs/superpowers/specs/2026-03-25-koh-assembler-design.md`
**Original plan:** `docs/superpowers/plans/2026-03-25-koh-implementation-plan.md`
**Parent index:** `docs/superpowers/plans/2026-04-02-koh-completion-plan.md`

**Recommended order:**
1. **Task 1 (Macro Arity)** — unblocks Plan 2 Task 5 (Signature Help), low risk
2. **Task 2 (Constraint Diagnostics)** — improves linker UX, independent of other tasks
3. **Task 3 (Parallel Parsing)** — performance improvement, low risk (lexer/parser are pure)
4. **Task 4 (AOT CLI)** — ship native binaries, independent
5. **Task 5 (AOT LSP Feasibility)** — assessment only, may not produce a binary
6. **Task 6 (Incremental Invalidation)** — highest complexity, most benefit for LSP responsiveness

**Exit criteria:** Macro arity flows through the pipeline. Linker produces actionable error messages. CLI tools publish as native binaries. Incremental reparse produces identical results to full reparse.

---

## Phase Overview

| Task | Milestone | You can now... |
|------|-----------|----------------|
| 1 | Macro Arity Metadata | Query macro call-site arity from the semantic pipeline |
| 2 | Constraint Diagnostics | See exactly which sections conflict and by how many bytes |
| 3 | Parallel Parsing | Multi-file projects parse concurrently on all cores |
| 4 | AOT: CLI Tools | Ship `koh-asm` and `koh-link` as single native binaries |
| 5 | AOT: LSP Feasibility | Determine whether `koh-lsp` can ship as AOT |
| 6 | Incremental Invalidation | Region-based reparse for single-region edits, safe fallback to full reparse |

---

## Task 1: Macro Call-Site Arity Metadata

**Milestone:** The binding pipeline tracks observed macro call-site arities, making them queryable by the LSP.

**Critical distinction:** RGBDS macros have **no declared parameter list**. A macro can accept any number of arguments — the body uses `\1`..`\9` and `_NARG` to access them, and different call sites can pass different argument counts. What we expose is the **maximum observed call-site arity** — a best-effort hint, not a language-level signature.

**Approach:** During expansion, `AssemblyExpander.ExpandMacroCall` already collects arguments via `CollectMacroArgs`. Track the max observed arg count per macro name. This is more accurate than scanning the body for `\N` references (which the v2 plan proposed — that was wrong because a macro using only `\1` but called with 3 args would report 1 instead of 3).

**Files:**
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`
- Modify: `src/Koh.Core/Binding/BindingResult.cs`
- Modify: `src/Koh.Core/Binding/EmitModel.cs`
- Test: `tests/Koh.Core.Tests/Binding/MacroMetadataTests.cs`

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
        var model = compilation.Emit();

        await Assert.That(model.MacroArities.ContainsKey("my_add")).IsTrue();
        await Assert.That(model.MacroArities["my_add"]).IsEqualTo(2);
    }

    [Test]
    public async Task MacroArity_VaryingCallSites_ReportsMax()
    {
        // Same macro called with 1 arg and then 3 args — should report 3
        var tree = SyntaxTree.Parse(
            "SECTION \"Main\", ROM0\n" +
            "flex: MACRO\n    nop\nENDM\n" +
            "    flex a\n" +
            "    flex a, b, c\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.Emit();

        await Assert.That(model.MacroArities["flex"]).IsEqualTo(3);
    }

    [Test]
    public async Task MacroArity_NoArgCall_Reports0()
    {
        var tree = SyntaxTree.Parse(
            "SECTION \"Main\", ROM0\n" +
            "init: MACRO\n    xor a\nENDM\n" +
            "    init\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.Emit();

        await Assert.That(model.MacroArities["init"]).IsEqualTo(0);
    }

    [Test]
    public async Task MacroArity_NoCalls_EmptyDictionary()
    {
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\nnop\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.Emit();

        await Assert.That(model.MacroArities).IsNotNull();
        await Assert.That(model.MacroArities.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MacroArity_DefinedButNeverCalled_NotInArities()
    {
        var tree = SyntaxTree.Parse(
            "SECTION \"Main\", ROM0\n" +
            "unused: MACRO\n    nop\nENDM\n" +
            "    nop\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.Emit();

        // Defined but never called — no observed arity
        await Assert.That(model.MacroArities.ContainsKey("unused")).IsFalse();
    }

    [Test]
    public async Task MacroArity_ParenGroupedArgs_CountsCorrectly()
    {
        // BANK(label), $42 → 2 args (parens group the first)
        var tree = SyntaxTree.Parse(
            "SECTION \"Main\", ROM0\n" +
            "my_call: MACRO\n    nop\nENDM\n" +
            "    my_call BANK(some_label), $42\n");
        var compilation = Compilation.Create(tree);
        var model = compilation.Emit();

        await Assert.That(model.MacroArities["my_call"]).IsEqualTo(2);
    }
}
```

- [ ] **Step 2: Track call-site arity in AssemblyExpander**

In `AssemblyExpander`, add a `Dictionary<string, int>` to track max observed arg count. In `ExpandMacroCall`, after `CollectMacroArgs`, update:

```csharp
// In ExpandMacroCall, after: var args = CollectMacroArgs(tokens, startIndex: 1);
if (!_macroArities.TryGetValue(name, out var existing) || args.Count > existing)
    _macroArities[name] = args.Count;
```

Expose via a public property: `public IReadOnlyDictionary<string, int> MacroArities => _macroArities;`

- [ ] **Step 3: Thread through BindingResult → EmitModel**

In `BindingResult`, add a new property:

```csharp
public IReadOnlyDictionary<string, int>? MacroArities { get; }
```

Update `BindingResult` constructor to accept and store it. In `Binder.Bind`, after expansion completes, pass `_expander.MacroArities` into the `BindingResult`.

In `EmitModel`, add:

```csharp
public IReadOnlyDictionary<string, int> MacroArities { get; }
```

In `EmitModel.FromBindingResult`, copy from `BindingResult`:

```csharp
var macroArities = result.MacroArities ?? new Dictionary<string, int>();
```

Update both `EmitModel` constructors to accept and store `macroArities`. The deserialization constructor (used by `KobjReader`) passes an empty dictionary.

- [ ] **Step 4: Run tests, verify pass**

```bash
cd /c/projekty/koh && dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Core/Binding/AssemblyExpander.cs src/Koh.Core/Binding/BindingResult.cs src/Koh.Core/Binding/Binder.cs src/Koh.Core/Binding/EmitModel.cs tests/Koh.Core.Tests/Binding/MacroMetadataTests.cs
git commit -m "feat: track macro call-site arity through binding pipeline"
```

---

## Task 2: Constraint Solver Diagnostics

**Milestone:** When section placement fails, the linker reports specific section names, sizes, bank numbers, and overflow amounts.

**Key constraint:** Diagnostic messages must match the actual allocator model. The current `SectionPlacer` uses a linear end-of-bank cursor — no fragmentation, no gaps. "Largest free space" means the distance from the bank cursor to the bank end, not a fragmentation-aware measure. The error message should say "free space" (not "contiguous free space") to be accurate to the model.

**Overlap detection timing:** Fixed-address overlaps are detected AFTER placement is committed (the section's `PlacedAddress` is set). To avoid duplicate diagnostics, overlap detection runs once per fixed-address section against all previously placed sections, not bidirectionally.

**Files:**
- Modify: `src/Koh.Linker.Core/SectionPlacer.cs`
- Test: `tests/Koh.Linker.Tests/ConstraintDiagnosticTests.cs`

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
        await Assert.That(errors.Count).IsGreaterThan(0);
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
        await Assert.That(errors.Count).IsGreaterThan(0);
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
        await Assert.That(errors.Count).IsGreaterThan(0);
        await Assert.That(errors[0].Message).Contains("bank 1");
    }

    [Test]
    public async Task Placement_OverflowAmount_Reported()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("A", SectionType.Rom0, size: 0x2000),
            CreateSection("B", SectionType.Rom0, size: 0x1800),
            CreateSection("C", SectionType.Rom0, size: 0x1000),
        }); // Total 0x4800, capacity 0x4000, overflow 2048

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsGreaterThan(0);
        await Assert.That(errors[0].Message).Contains("2048");
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
        await Assert.That(errors.Count).IsGreaterThan(0);
        await Assert.That(errors[0].Message).Contains("overlap");
        await Assert.That(errors[0].Message).Contains("vectors");
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

In `SectionPlacer.PlaceRegion`, after committing a fixed-address placement (`section.PlacedAddress = section.FixedAddress.Value`), check for overlaps:

```csharp
// After setting PlacedAddress/PlacedBank for a fixed-address section:
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

Replace the generic `"does not fit in {Type} memory region"` in the `if (!placed)` block:

```csharp
if (section.Data.Length > bankCapacity)
{
    _diagnostics.Report(default,
        $"Section '{section.Name}' ({section.Data.Length} bytes) exceeds " +
        $"the {bankCapacity}-byte capacity of {section.Type}");
}
else if (targetBank >= 0)
{
    int used = bankUsage.GetValueOrDefault(targetBank, region.StartAddress)
               - region.StartAddress;
    int overflow = used + section.Data.Length - bankCapacity;
    _diagnostics.Report(default,
        $"Section '{section.Name}' ({section.Data.Length} bytes) does not fit in " +
        $"{section.Type} bank {targetBank} — {used} of {bankCapacity} bytes used, " +
        $"overflow by {overflow} bytes");
}
else
{
    int bestFree = 0;
    for (int b = firstBank; b < bankCount; b++)
    {
        int free = region.EndAddress
                   - bankUsage.GetValueOrDefault(b, region.StartAddress);
        if (free > bestFree) bestFree = free;
    }
    _diagnostics.Report(default,
        $"Section '{section.Name}' ({section.Data.Length} bytes) does not fit in any " +
        $"{section.Type} bank — largest free space is {bestFree} bytes");
}
```

- [ ] **Step 4: Run tests, verify pass**

```bash
cd /c/projekty/koh && dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Linker.Core/SectionPlacer.cs tests/Koh.Linker.Tests/ConstraintDiagnosticTests.cs
git commit -m "feat(linker): add detailed constraint solver diagnostics"
```

---

## Task 3: Parallel Parsing

**Milestone:** `Compilation.CreateFromSources()` parses multiple files concurrently. A useful optimization for CLI compilation.

**Thread safety assumption:** Parsing and lexing are free of shared mutable state. Verified: `Lexer.Keywords` is `static readonly` (populated at class init, never mutated). All Parser/Lexer static members are either `readonly` or pure functions. `Parallel.For` is safe.

**Files:**
- Modify: `src/Koh.Core/Compilation.cs`
- Test: `tests/Koh.Core.Tests/ParallelParseTests.cs`

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
            sources[i] = SourceText.From($"SECTION \"S{i}\", ROMX\nlabel_{i}:\n    ld a, {i % 256}\n", $"file_{i}.asm");

        var compilation = Compilation.CreateFromSources(sources);
        await Assert.That(compilation.SyntaxTrees.Count).IsEqualTo(50);
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

- [ ] **Step 3: Run tests, verify pass**

```bash
cd /c/projekty/koh && dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Core/Compilation.cs tests/Koh.Core.Tests/ParallelParseTests.cs
git commit -m "feat: add parallel multi-file parsing via Compilation.CreateFromSources"
```

---

## Task 4: Native AOT for CLI Tools

**Milestone:** `koh-asm` and `koh-link` publish as single native binaries.

**Scope:** CLI tools only. LSP is Task 5.

**Verification:** The plan includes a documented publish profile and a smoke test script.

**Files:**
- Modify: `src/Koh.Asm/Koh.Asm.csproj`
- Modify: `src/Koh.Link/Koh.Link.csproj`

- [ ] **Step 1: Enable `<PublishAot>true</PublishAot>` in both CLI csprojs**

- [ ] **Step 2: Publish and fix warnings**

Windows:
```bash
cd /c/projekty/koh
dotnet publish src/Koh.Asm -c Release -r win-x64 -o publish/win-x64/asm
dotnet publish src/Koh.Link -c Release -r win-x64 -o publish/win-x64/link
```

Fix any trimming/AOT warnings.

- [ ] **Step 3: Smoke test the published binaries (Git Bash)**

```bash
cd /c/projekty/koh
echo 'SECTION "ROM", ROM0' > test-aot.asm && echo 'nop' >> test-aot.asm
publish/win-x64/asm/koh-asm.exe test-aot.asm -o test-aot.kobj
publish/win-x64/link/koh-link.exe test-aot.kobj -o test-aot.gb
ls -la test-aot.gb
rm test-aot.asm test-aot.kobj test-aot.gb
```

Verify: output file exists and has size > 0. Full correctness is covered by existing integration tests.

- [ ] **Step 4: Add publish profile for reproducibility**

Create `src/Koh.Asm/Properties/PublishProfiles/win-x64-aot.pubxml` (and for `Koh.Link`):

```xml
<Project>
  <PropertyGroup>
    <PublishAot>true</PublishAot>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <Configuration>Release</Configuration>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Asm/ src/Koh.Link/
git commit -m "feat: enable Native AOT for koh-asm and koh-link with publish profiles"
```

---

## Task 5: Native AOT Feasibility for LSP

**Milestone:** Determine whether `koh-lsp` can be published as a Native AOT binary. Deliverable: a written decision with evidence (build log or working binary).

- [ ] **Step 1: Attempt AOT publish**

```bash
cd /c/projekty/koh
dotnet publish src/Koh.Lsp -c Release -r win-x64 2>&1 | tee lsp-aot-log.txt
```

- [ ] **Step 2: Assess and document result**

If it publishes:
- Test: launch the binary, connect VS Code, verify basic LSP operations
- If working: enable `<PublishAot>true</PublishAot>` in Koh.Lsp.csproj

If it fails:
- Catalog the warnings/errors from `lsp-aot-log.txt`
- Identify root cause (likely `Newtonsoft.Json` reflection in `StreamJsonRpc`)
- Estimate effort to fix (replace transport, add source generators, etc.)
- Decision: fix now, defer, or accept non-AOT LSP

- [ ] **Step 3: Commit decision**

```bash
# If AOT works:
git add src/Koh.Lsp/Koh.Lsp.csproj
git commit -m "feat: enable Native AOT for koh-lsp"

# If not feasible, write a decision note:
# Create docs/decisions/lsp-aot-decision.md with blockers, effort estimate, and recommendation
git add docs/decisions/lsp-aot-decision.md lsp-aot-log.txt
git commit -m "docs: document LSP AOT infeasibility — Newtonsoft.Json/StreamJsonRpc blockers"
```

---

## Task 6: Incremental Document Invalidation

**Milestone:** Region-based reparse attempted for single-region edits, with safe fallback to full reparse. Statement reuse is an optimization detail, not a guaranteed contract. Multi-region edits and ambiguous cases fall back to full reparse.

**Explicit limitations:**
- Only reuses at top-level statement granularity (children of CompilationUnit)
- Only supports a single contiguous change. `Workspace.ChangeDocument` receives full text — `ComputeChange` derives the single change region. If it can't (identical text or pathological diff), falls back to full reparse.
- Falls back to full reparse when: no children overlap the edit, the edit spans all children, the stitched tree's total width doesn't match the new text length, or reparse start/end are out of bounds.
- Does NOT guarantee green node reuse as a stable API contract — it's a performance optimization. Tests verify correctness (identical output to full parse), not reuse.

**Files:**
- Modify: `src/Koh.Core/Syntax/SyntaxTree.cs`
- Create: `src/Koh.Core/Syntax/IncrementalParser.cs`
- Modify: `src/Koh.Lsp/Workspace.cs`
- Test: `tests/Koh.Core.Tests/Syntax/IncrementalParseTests.cs`

- [ ] **Step 1: Write failing tests**

Tests verify correctness (identical tokens and diagnostics to full parse), not implementation details. Include tests with comments/trivia around edit boundaries.

```csharp
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests.Syntax;

public class IncrementalParseTests
{
    [Test]
    public async Task IncrementalParse_EditMiddle_MatchesFull()
    {
        var source = SourceText.From("nop\nhalt\nstop\n");
        var tree = SyntaxTree.Parse(source);

        var edit = new TextChange(new TextSpan(4, 4), "ld a, b");
        var newSource = source.WithChanges(edit);
        var incTree = tree.WithChanges(edit, newSource);
        var fullTree = SyntaxTree.Parse(newSource);

        await AssertTreesEqual(incTree, fullTree);
    }

    [Test]
    public async Task IncrementalParse_InsertLine_MatchesFull()
    {
        var source = SourceText.From("nop\nstop\n");
        var tree = SyntaxTree.Parse(source);

        var edit = new TextChange(new TextSpan(4, 0), "halt\n");
        var newSource = source.WithChanges(edit);
        var incTree = tree.WithChanges(edit, newSource);
        var fullTree = SyntaxTree.Parse(newSource);

        await AssertTreesEqual(incTree, fullTree);
    }

    [Test]
    public async Task IncrementalParse_DeleteLine_MatchesFull()
    {
        var source = SourceText.From("nop\nhalt\nstop\n");
        var tree = SyntaxTree.Parse(source);

        var edit = new TextChange(new TextSpan(4, 5), "");
        var newSource = source.WithChanges(edit);
        var incTree = tree.WithChanges(edit, newSource);
        var fullTree = SyntaxTree.Parse(newSource);

        await AssertTreesEqual(incTree, fullTree);
    }

    [Test]
    public async Task IncrementalParse_EditWithError_MatchesDiagnostics()
    {
        var source = SourceText.From("nop\nhalt\nstop\n");
        var tree = SyntaxTree.Parse(source);

        var edit = new TextChange(new TextSpan(4, 4), "ld a,");
        var newSource = source.WithChanges(edit);
        var incTree = tree.WithChanges(edit, newSource);
        var fullTree = SyntaxTree.Parse(newSource);

        await Assert.That(incTree.Diagnostics.Count)
            .IsEqualTo(fullTree.Diagnostics.Count);
        for (int i = 0; i < incTree.Diagnostics.Count; i++)
        {
            await Assert.That(incTree.Diagnostics[i].Span.Start)
                .IsEqualTo(fullTree.Diagnostics[i].Span.Start);
            await Assert.That(incTree.Diagnostics[i].Span.Length)
                .IsEqualTo(fullTree.Diagnostics[i].Span.Length);
        }
    }

    [Test]
    public async Task IncrementalParse_EditNearComment_MatchesFull()
    {
        var text = "; header comment\nnop\nhalt ; inline\nstop\n";
        var source = SourceText.From(text);
        var tree = SyntaxTree.Parse(source);

        // Find "halt ; inline" dynamically — avoids brittle hard-coded span
        int editStart = text.IndexOf("halt ; inline");
        int editLen = "halt ; inline".Length;
        var edit = new TextChange(new TextSpan(editStart, editLen), "ld a, b");
        var newSource = source.WithChanges(edit);
        var incTree = tree.WithChanges(edit, newSource);
        var fullTree = SyntaxTree.Parse(newSource);

        await AssertTreesEqual(incTree, fullTree);
    }

    [Test]
    public async Task IncrementalParse_ReplaceAll_FallsBackCorrectly()
    {
        var source = SourceText.From("nop\nhalt\n");
        var tree = SyntaxTree.Parse(source);

        var edit = new TextChange(new TextSpan(0, source.Length), "stop\ndi\n");
        var newSource = source.WithChanges(edit);
        var incTree = tree.WithChanges(edit, newSource);
        var fullTree = SyntaxTree.Parse(newSource);

        await AssertTreesEqual(incTree, fullTree);
    }

    [Test]
    public async Task IncrementalParse_EditFirstLine_MatchesFull()
    {
        var source = SourceText.From("nop\nhalt\nstop\n");
        var tree = SyntaxTree.Parse(source);

        var edit = new TextChange(new TextSpan(0, 3), "ld a, b");
        var newSource = source.WithChanges(edit);
        var incTree = tree.WithChanges(edit, newSource);
        var fullTree = SyntaxTree.Parse(newSource);

        await AssertTreesEqual(incTree, fullTree);
    }

    [Test]
    public async Task IncrementalParse_EditLastLine_MatchesFull()
    {
        var source = SourceText.From("nop\nhalt\nstop\n");
        var tree = SyntaxTree.Parse(source);

        var edit = new TextChange(new TextSpan(9, 4), "di\n");
        var newSource = source.WithChanges(edit);
        var incTree = tree.WithChanges(edit, newSource);
        var fullTree = SyntaxTree.Parse(newSource);

        await AssertTreesEqual(incTree, fullTree);
    }

    [Test]
    public async Task IncrementalParse_NodePositions_AreCorrect()
    {
        var source = SourceText.From("nop\nhalt\nstop\n");
        var tree = SyntaxTree.Parse(source);

        var edit = new TextChange(new TextSpan(4, 4), "ld a, b");
        var newSource = source.WithChanges(edit);
        var incTree = tree.WithChanges(edit, newSource);
        var fullTree = SyntaxTree.Parse(newSource);

        // Check that node positions match
        var incPositions = CollectNodePositions(incTree.Root);
        var fullPositions = CollectNodePositions(fullTree.Root);
        await Assert.That(incPositions).IsEqualTo(fullPositions);
    }

    [Test]
    public async Task IncrementalParse_NodeKindsMatch()
    {
        var source = SourceText.From("nop\nhalt\nstop\n");
        var tree = SyntaxTree.Parse(source);

        var edit = new TextChange(new TextSpan(4, 4), "ld a, b");
        var newSource = source.WithChanges(edit);
        var incTree = tree.WithChanges(edit, newSource);
        var fullTree = SyntaxTree.Parse(newSource);

        var incKinds = CollectNodeKinds(incTree.Root);
        var fullKinds = CollectNodeKinds(fullTree.Root);
        await Assert.That(incKinds).IsEqualTo(fullKinds);
    }

    [Test]
    public async Task IncrementalParse_RepeatedSubstring_Correct()
    {
        // Both lines identical — tests ComputeChange with ambiguous prefix/suffix
        var source = SourceText.From("nop\nnop\n");
        var tree = SyntaxTree.Parse(source);

        var edit = new TextChange(new TextSpan(0, 3), "halt");
        var newSource = source.WithChanges(edit);
        var incTree = tree.WithChanges(edit, newSource);
        var fullTree = SyntaxTree.Parse(newSource);

        await AssertTreesEqual(incTree, fullTree);
    }

    private static async Task AssertTreesEqual(SyntaxTree a, SyntaxTree b)
    {
        var aTokens = CollectTokenTexts(a.Root);
        var bTokens = CollectTokenTexts(b.Root);
        await Assert.That(aTokens).IsEqualTo(bTokens);
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

    private static List<int> CollectNodePositions(SyntaxNode node)
    {
        var positions = new List<int>();
        CollectPositionWalk(node, positions);
        return positions;
    }

    private static void CollectPositionWalk(SyntaxNode node, List<int> positions)
    {
        positions.Add(node.Position);
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
                CollectPositionWalk(child.AsNode!, positions);
        }
    }

    private static List<SyntaxKind> CollectNodeKinds(SyntaxNode node)
    {
        var kinds = new List<SyntaxKind>();
        CollectKindsWalk(node, kinds);
        return kinds;
    }

    private static void CollectKindsWalk(SyntaxNode node, List<SyntaxKind> kinds)
    {
        kinds.Add(node.Kind);
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
                CollectKindsWalk(child.AsNode!, kinds);
        }
    }
}
```

- [ ] **Step 2: Implement IncrementalParser**

Same algorithm as v2 but with more conservative fallback conditions and explicit width validation.

- [ ] **Step 3: Add `WithChanges(TextChange, SourceText)` to SyntaxTree**

Explicit API — takes the change and new text, does not try to infer the change.

- [ ] **Step 4: Wire into Workspace with `ComputeChange`**

`ComputeChange` derives a single contiguous change from old/new text. If texts are identical, returns null. The workspace calls `tree.WithChanges(change, newSource)` when a single change is found, otherwise falls back to full reparse.

- [ ] **Step 5: Run tests, verify pass**

ALL tests pass — incremental and full produce identical token sequences, diagnostic spans, node kinds, and node positions.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Core/Syntax/SyntaxTree.cs src/Koh.Core/Syntax/IncrementalParser.cs src/Koh.Lsp/Workspace.cs tests/Koh.Core.Tests/Syntax/IncrementalParseTests.cs
git commit -m "feat: add statement-level incremental reparsing for LSP responsiveness"
```

---
