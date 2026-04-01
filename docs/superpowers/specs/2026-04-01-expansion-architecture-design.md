# AssemblyExpander Compiler-Grade Expansion Architecture

**Date:** 2026-04-01
**Status:** Approved design, pending implementation

## Problem

`AssemblyExpander` is a ~2,000 line class with 15+ mutable ambient fields that flattens macros, REPT/FOR loops, conditionals, and includes into a flat `List<ExpandedNode>`. Its architecture has three core problems:

1. **Text-replay dominance.** REPT, FOR, parameterized macros, and EQUS all follow the same path: extract raw text → substitute strings → `SyntaxTree.Parse` → recurse. Text replay is the default execution model, not an exceptional fallback.

2. **Ambient mutable state.** Expansion scope (file path, source text, origin, depth, macro frame) is tracked through ~9 mutable fields with manual save/restore in try/finally blocks across 6+ methods. Scope ownership is implicit in field mutation order, not explicit in method contracts.

3. **Flat provenance.** `ExpansionOrigin` records only the immediate expansion cause (one kind + one span). There is no ancestry chain. A node produced by a macro inside a REPT inside an INCLUDE has the same provenance depth as a node from a simple macro call.

## Objective

Transform `AssemblyExpander` from a text-rewrite-heavy preprocessor toward a compiler-grade expansion pipeline:

```
Source syntax
  → structured expansion with explicit context
  → effective statements with rich provenance traces
  → binder consumes flat effective program (unchanged contract)
```

This pass improves the internal expansion architecture without yet replacing the flat `List<ExpandedNode>` contract with a richer `ExpandedCompilation` model. That is a deliberate scope boundary — enriching per-node metadata and internal structure first, changing the binder contract later.

## Approach

Two-phase incremental migration. Phase 1 establishes immutable `ExpansionContext` as the dominant ownership model. Phase 2 builds on that context to add structured replay and extract a dedicated `TextReplayService`.

The transitional bridge in Phase 1 (context parameter added before all ambient reads are migrated) exists only inside Phase 1 and must be removed before any Phase 2 work begins.

---

## Phase 1: ExpansionContext and State Elimination

### ExpansionTrace

Immutable array-backed expansion ancestry. Each `ExpandedNode` carries its full trace at emission time.

```csharp
internal sealed record ExpansionTrace(ImmutableArray<ExpansionFrame> Frames)
{
    public static readonly ExpansionTrace Empty = new([]);
    public bool IsEmpty => Frames.IsDefaultOrEmpty;
    public ExpansionFrame? Current => IsEmpty ? null : Frames[^1];
    public int Depth => Frames.Length;

    public ExpansionTrace Push(ExpansionFrame frame) => new(Frames.Add(frame));

    public bool ContainsKind(ExpansionKind kind)
    {
        foreach (var f in Frames)
            if (f.Kind == kind) return true;
        return false;
    }

    public ExpansionFrame? FindNearest(ExpansionKind kind)
    {
        for (int i = Frames.Length - 1; i >= 0; i--)
            if (Frames[i].Kind == kind) return Frames[i];
        return null;
    }
}
```

### ExpansionFrame

One level of expansion ancestry with per-kind data. `TextReplayReason` is a structured enum.

```csharp
internal enum ExpansionKind
{
    Source,
    MacroExpansion,
    ReptIteration,
    ForIteration,
    Include,
    TextReplay
}

internal enum TextReplayReason
{
    MacroParameterConcatenation,
    ReptUniqueLabelSubstitution,
    ForTokenShapingSubstitution,
    EqusReplay
}

internal sealed record ExpansionFrame(
    ExpansionKind Kind,
    string FilePath,
    TextSpan SourceSpan,
    string? Name = null,
    int? Iteration = null,
    TextReplayReason? ReplayReason = null)
{
    public static ExpansionFrame ForMacro(MacroDefinition macro)
        => new(ExpansionKind.MacroExpansion, macro.DefinitionFilePath,
               macro.DefinitionSpan, macro.Name);

    public static ExpansionFrame ForRept(string filePath, TextSpan span, int iteration)
        => new(ExpansionKind.ReptIteration, filePath, span, Iteration: iteration);

    public static ExpansionFrame ForFor(string filePath, TextSpan span,
        string? varName, int iteration)
        => new(ExpansionKind.ForIteration, filePath, span, varName, iteration);

    public static ExpansionFrame ForInclude(string filePath, TextSpan span)
        => new(ExpansionKind.Include, filePath, span);

    public static ExpansionFrame ForTextReplay(string filePath, TextSpan sourceSpan,
        TextReplayReason reason)
        => new(ExpansionKind.TextReplay, filePath, sourceSpan, ReplayReason: reason);
}
```

### ExpansionContext

Immutable record passed through all expansion methods. Child contexts derived via factory methods — no mutation, no save/restore.

Depth is split into `StructuralDepth` (macro + include nesting) and `ReplayDepth` (text→parse→recurse nesting). These are checked independently — the old combined `_expansionDepth` is removed entirely, not preserved as a synthetic sum.

Macro frames use `ImmutableStack<MacroFrame>` for nested macro parent lookup. `MacroFrame` itself remains a mutable class — SHIFT mutates the frame object within its owning scope. The immutable stack provides structural ownership (visibility); frame internals are mutable for SHIFT. This is a pragmatic compromise documented explicitly.

Structured parsed-body replay (e.g., param-free macro fast path) does **not** increment `ReplayDepth`. Only text→parse→recurse increments `ReplayDepth`. `ExpandParsedTree` only updates `SourceText` on the context — this is structural execution, not replay.

```csharp
internal sealed record ExpansionContext
{
    public SourceText? SourceText { get; init; }
    public string FilePath { get; init; } = "";
    public ExpansionTrace Trace { get; init; } = ExpansionTrace.Empty;
    public ImmutableStack<MacroFrame> MacroFrames { get; init; } = ImmutableStack<MacroFrame>.Empty;
    public int StructuralDepth { get; init; }
    public int ReplayDepth { get; init; }
    public int MacroBodyDepth { get; init; }
    public int LoopDepth { get; init; }

    public MacroFrame? CurrentMacroFrame
        => MacroFrames.IsEmpty ? null : MacroFrames.Peek();

    public ExpansionContext ForMacro(MacroFrame frame, MacroDefinition macro)
        => this with
        {
            MacroFrames = MacroFrames.Push(frame),
            MacroBodyDepth = MacroBodyDepth + 1,
            StructuralDepth = StructuralDepth + 1,
            Trace = Trace.Push(ExpansionFrame.ForMacro(macro))
        };

    public ExpansionContext ForLoop(ExpansionFrame loopFrame)
        => this with
        {
            LoopDepth = LoopDepth + 1,
            Trace = Trace.Push(loopFrame)
        };

    public ExpansionContext ForInclude(string filePath, SourceText source, TextSpan directiveSpan)
        => this with
        {
            SourceText = source,
            FilePath = filePath,
            StructuralDepth = StructuralDepth + 1,
            Trace = Trace.Push(ExpansionFrame.ForInclude(filePath, directiveSpan))
        };

    public ExpansionContext ForTextReplay(SourceText replaySource, TextSpan triggerSpan,
        TextReplayReason reason)
        => this with
        {
            SourceText = replaySource,
            ReplayDepth = ReplayDepth + 1,
            Trace = Trace.Push(ExpansionFrame.ForTextReplay(FilePath, triggerSpan, reason))
        };
}
```

### LoopControl

```csharp
internal enum LoopControl { Continue, Break }
```

`ExpandBodyList` returns `LoopControl`. On BREAK, returns `LoopControl.Break` immediately. Loop callers check the return. Outside loops, the return value should not be `Break` — if it is, that indicates an accidental propagation bug.

### ExpandedNode

```csharp
public sealed record ExpandedNode(
    SyntaxNode Node,
    string SourceFilePath = "",
    bool WasInConditional = false,
    bool FromMacroBody = false,
    ExpansionTrace? Trace = null);
```

**`SourceFilePath`**: the file path of the immediate source container backing the emitted SyntaxNode. Used by the binder for INCBIN resolution and diagnostic file attribution. This is NOT the original authored file — trace ancestry carries that.

**`FromMacroBody`**: kept as a denormalized convenience flag. Derivable from `Trace.ContainsKind(ExpansionKind.MacroExpansion)` but retained for binder hot-path compatibility in this pass.

**`ExpansionOrigin`**: deleted. `ExpansionOrigin.cs` removed. All consumers migrate to `Trace`.

Binder consumers should use `Trace` for rich ancestry inspection (`FindNearest`, `ContainsKind`, full frame enumeration), not only the shallow `Trace?.Current?.Kind` replacement for `Origin?.Kind`.

### Method signatures

Every expansion method gains `ExpansionContext ctx`:

```
ExpandBodyList(siblings, ref i, output, ctx) → LoopControl
ExpandMacroCall(node, output, ctx)
ExpandMacroBody(macro, frame, output, ctx)
ExpandParsedTree(tree, output, ctx)
ExpandInclude(node, output, ctx)
ExpandRept(reptNode, siblings, ref i, output, ctx)
ExpandFor(forNode, siblings, ref i, output, ctx)
ExpandTextInline(text, output, ctx, triggerSpan, reason)
HandleConditional(node, ctx)
EarlyDefineEqu(node, ctx)
EarlyProcessCharmap(node, ctx)
HandlePurge(node, ctx)
HandleRsDirective(node, ctx)
```

Child context creation replaces save/restore. Example:

```csharp
private void ExpandMacroCall(SyntaxNode node, List<ExpandedNode> output, ExpansionContext ctx)
{
    // ... resolve name, args, create frame ...
    var macroCtx = ctx.ForMacro(frame, macro);
    if (macroCtx.StructuralDepth > MaxStructuralDepth)
    {
        _diagnostics.Report(node.FullSpan, ...);
        return;
    }
    ExpandMacroBody(macro, frame, output, macroCtx);
    // caller continues with ctx — macroCtx is discarded naturally
}
```

No `PopMacroFrame()` method. No explicit stack pop. The parent context variable (`ctx`) is naturally back in scope after the child call returns.

### Fields eliminated

| Field | Replacement |
|-------|-------------|
| `_currentSourceText` | `ctx.SourceText` |
| `_currentFilePath` | `ctx.FilePath` |
| `_currentOrigin` | `ctx.Trace` |
| `_breakRequested` | `LoopControl` return value |
| `_expansionDepth` | `ctx.StructuralDepth` and `ctx.ReplayDepth` (checked independently) |
| `_macroBodyDepth` | `ctx.MacroBodyDepth` |
| `_loopDepth` | `ctx.LoopDepth` |
| `_macroFrameStack` | `ctx.MacroFrames` |
| `_reptUniqueIdStack` | Eliminated. Macro unique ID lives on `MacroFrame.UniqueId`. REPT/FOR iteration unique ID is a local `int` allocated from `_uniqueIdCounter` at iteration entry, passed to the text replay path that performs `\@` substitution. No stack. |

### Fields that remain on AssemblyExpander

Genuinely shared mutable state:

| Field | Why it stays |
|-------|-------------|
| `_diagnostics` | Shared diagnostic sink |
| `_symbols` | Shared symbol table |
| `_conditional` | Conditional assembly state machine |
| `_macros` | Macro repository (Phase 2 extraction candidate) |
| `_equsConstants` | EQUS constant store |
| `_charMaps` | Charmap state |
| `_rsCounter` | RS counter (order-dependent) |
| `_fileResolver` | Immutable dependency |
| `_includeStack` | Circular include detection |
| `_uniqueIdCounter` | Monotonic counter for `\@` |
| `_printOutput` | Output sink |
| `_interpolation` | Interpolation resolver |
| `_expressionCache` | Parse cache |

### Depth checking

Two separate limits:

```csharp
private const int MaxStructuralDepth = 64;
private const int MaxReplayDepth = 64;
```

Checked at context creation call sites (before entering the child scope), using the triggering node's span for diagnostics.

### Node emission

```csharp
output.Add(new ExpandedNode(node, ctx.FilePath, _conditional.Depth > 0,
    ctx.MacroBodyDepth > 0, ctx.Trace));
```

### Entry point

```csharp
public List<ExpandedNode> Expand(SyntaxTree tree)
{
    var ctx = new ExpansionContext
    {
        SourceText = tree.Text,
        FilePath = tree.Text.FilePath
    };
    // ... pre-scan, then:
    int i = 0;
    ExpandBodyList(children, ref i, output, ctx);
    return output;
}
```

---

## Phase 2: Structured Replay and TextReplayService

### Body classification

Before expanding a loop body, classify it to determine the expansion strategy:

```csharp
internal enum BodyReplayKind
{
    Structural,
    RequiresTextReplay
}

internal sealed record BodyReplayPlan(
    BodyReplayKind Kind,
    TextReplayReason? Reason = null);
```

**REPT classification:**
- Body text contains `\@` → `RequiresTextReplay` with reason `ReptUniqueLabelSubstitution`
- Otherwise → `Structural`

**FOR classification:**
- Parse body once. Walk token stream. For each occurrence of the loop variable:
  - If the variable appears as a distinct `IdentifierToken` that represents a standalone symbolic reference (a complete token in the parsed tree, not a fragment of a synthesized token) → evaluation-only, structural
  - If the variable participates in text constructs that synthesize token identity or token boundaries (EQUS/text replay cases, macro parameter concatenation, `\@` suffix) → token-shaping, requires text replay
- If any token-shaping occurrence exists → `RequiresTextReplay` with reason `ForTokenShapingSubstitution`
- If body contains `\@` → `RequiresTextReplay` with reason `ReptUniqueLabelSubstitution`
- Otherwise → `Structural`

Classification is conservative: ambiguous cases default to `RequiresTextReplay`. False positives toward replay are safe (unnecessary reparse); false positives toward structural are bugs (wrong tokens).

### Structured REPT replay

When `BodyReplayKind.Structural`:

```csharp
private void ExpandReptStructural(List<SyntaxNodeOrToken> bodyNodes, int count,
    SyntaxNode reptNode, List<ExpandedNode> output, ExpansionContext ctx)
{
    for (int iter = 0; iter < count; iter++)
    {
        var iterFrame = ExpansionFrame.ForRept(ctx.FilePath, reptNode.FullSpan, iter);
        var iterCtx = ctx.ForLoop(iterFrame);
        int j = 0;
        var result = ExpandBodyList(bodyNodes, ref j, output, iterCtx);
        if (result == LoopControl.Break) break;
    }
}
```

No text extraction. No `SyntaxTree.Parse`. No `\@` substitution. Parsed body nodes walked directly N times.

### Structured FOR replay

When `BodyReplayKind.Structural`:

```csharp
private void ExpandForStructural(List<SyntaxNodeOrToken> bodyNodes,
    string varName, long start, long stop, long step,
    SyntaxNode forNode, List<ExpandedNode> output, ExpansionContext ctx)
{
    int iter = 0;
    for (long v = start; step > 0 ? v < stop : v > stop; v += step, iter++)
    {
        _symbols.DefineOrRedefine(varName, v);
        var iterFrame = ExpansionFrame.ForFor(ctx.FilePath, forNode.FullSpan, varName, iter);
        var iterCtx = ctx.ForLoop(iterFrame);
        int j = 0;
        var result = ExpandBodyList(bodyNodes, ref j, output, iterCtx);
        if (result == LoopControl.Break) break;
    }
}
```

The loop variable is updated in the shared symbol table before each iteration. Any expansion-time evaluation in the replayed body observes that value (conditionals, EQU expressions, RS counters, etc.). The binder later sees the resulting effective statements as usual.

### TextReplayService

Dedicated class owning all text→reparse expansion:

```csharp
internal sealed class TextReplayService
{
    private readonly DiagnosticBag _diagnostics;
    private readonly InterpolationResolver _interpolation;

    public string SubstituteUniqueId(string bodyText, int uniqueId)
        => bodyText.Replace("\\@", $"_{uniqueId}");

    public string SubstituteMacroParams(string body, MacroFrame frame, bool containsShift,
        SymbolTable symbols, ExpressionCache cache) { ... }

    public SyntaxTree? ParseForReplay(string text, bool hasMacroParams,
        ExpansionContext ctx, TextSpan triggerSpan, TextReplayReason reason,
        int maxReplayDepth)
    {
        if (!hasMacroParams)
            text = _interpolation.Resolve(text);
        if (ctx.ReplayDepth >= maxReplayDepth)
        {
            _diagnostics.Report(triggerSpan,
                $"Maximum text replay depth ({maxReplayDepth}) exceeded");
            return null;
        }
        return SyntaxTree.Parse(text);
    }

    // Moved from AssemblyExpander:
    // SubstituteParamReferences(...)
    // ResolveComputedArgs(...)
    // SubstituteOutsideStrings(...)
    // SubstituteAtPositions(...)
    // ContainsUnresolvedMacroParam(...)
    // ExtractBodyText(...)
}
```

### Methods moved to TextReplayService

| Method | Role |
|--------|------|
| `SubstituteParamReferences` | Core `\1..\9`, `\#`, `_NARG` text substitution |
| `ResolveComputedArgs` | `\<expr>` computed arg index |
| `SubstituteMacroParams` | Macro param substitution orchestration |
| `SubstituteOutsideStrings` | Regex substitution skipping string literals |
| `SubstituteAtPositions` | Position-based FOR variable substitution (for text-replay FOR path) |
| `ContainsUnresolvedMacroParam` | Detection of unresolved `\1..\9`, `\#` |
| `ExtractBodyText` | Source text extraction for loop bodies selected for replay |

### Architectural invariant

**No replay-driven parse may occur outside `TextReplayService.ParseForReplay`.**

`SyntaxTree.Parse` calls that remain in `AssemblyExpander` or other code are exclusively:

| Call site | Classification |
|-----------|---------------|
| `MacroDefinition` constructor | Definition-time analysis |
| `ExpandInclude` | New-source parsing (not replay) |
| FOR body classification parse | One-time classification analysis |

These are not replay — they are analysis or new-source intake. The boundary is: if the parse exists because text was transformed by substitution and needs re-lexing, it must go through `ParseForReplay`.

### Remaining reparses — exhaustive list

| Reparse site | Reason | Routed through |
|---|---|---|
| Macro body with param concatenation | `\1`..`\9`, `\@` create new tokens | `TextReplayService.ParseForReplay` |
| REPT body with `\@` | Unique suffix creates new identifiers | `TextReplayService.ParseForReplay` |
| FOR body with token-shaping variable | Variable synthesizes new token boundaries | `TextReplayService.ParseForReplay` |
| EQUS bare-name expansion | Text constant replayed as statements | `TextReplayService.ParseForReplay` |
| Lazy macro param resolution (SHIFT) | Deferred `\1..\9` creates new tokens after SHIFT | `TextReplayService.ParseForReplay` |
| `{symbol}` interpolation changing tokenization | Interpolated value creates new token structure | `TextReplayService.ParseForReplay` |

### AssemblyExpander after Phase 2

Approximate change: drops from ~2,000 lines to ~1,700 lines. More importantly, the remaining code is expansion orchestration with clear control flow — routing between structural and text-replay paths — rather than interleaved text manipulation.

`MacroDefinition.cs` is architecturally unchanged unless minor support for replay classification or cached body analysis is needed during implementation.

---

## Migration Steps

### Phase 1: Context threading

Each step leaves all 922 existing tests passing.

1. **Add new type files** — `ExpansionContext.cs`, `ExpansionTrace.cs`, `BodyReplayPlan.cs`. No references yet.
2. **Add `ctx` parameter to `ExpandBodyList`** — entry point creates initial context. Internals still temporarily read ambient fields (transitional bridge).
3. **Migrate `ExpandBodyList` internals to read from `ctx`** — one field at a time. Each ambient field removed from class after all reads migrated.
4. **Thread `ctx` through macro expansion** — child context replaces save/restore. `_macroFrameStack` removed.
5. **Thread `ctx` through include expansion** — child context replaces file path save/restore.
6. **Thread `ctx` through REPT/FOR** — loop context derivation replaces `_loopDepth++/--`.
7. **Replace `_breakRequested` with `LoopControl` return** — field removed.
8. **Thread `ctx` through remaining methods** — conditional, EQU, charmap, PURGE, RS.
9. **Remove all transitional bridge code** — verify no ambient expansion-scope fields remain. Only shared-state fields survive.
10. **Delete `ExpansionOrigin.cs`** — update `ExpandedNode`, update `Binder.cs` read sites.

**Gate: Phase 1 bridge must be fully removed before any Phase 2 work begins.**

### Phase 2: Structured replay and service extraction

11. **Create `TextReplayService`** — extract text substitution methods. `AssemblyExpander` delegates to it.
12. **Add body classification** — `BodyReplayPlan ClassifyBody(...)` for REPT and FOR.
13. **Add structured REPT replay** — `ExpandReptStructural`. Route based on classification.
14. **Add structured FOR replay** — `ExpandForStructural`. Route based on classification.
15. **Enforce architectural invariant** — verify no replay-driven `SyntaxTree.Parse` outside `TextReplayService.ParseForReplay`.

---

## Test Strategy

### Existing tests

All 922 tests must pass after every migration step. Primary regression guard.

### New tests

| Test | Validates |
|------|-----------|
| REPT without `\@` uses structural replay | Body nodes replayed directly, no reparse. Trace has no `TextReplay` frame. |
| FOR with standalone variable uses structural replay | Symbol table updated per iteration, body replayed structurally. Trace has no `TextReplay` frame. |
| FOR with concatenative variable falls back to text replay | Classification detects token-shaping, routes to `TextReplayService`. |
| Expansion trace carries full ancestry | Nested macro-inside-REPT-inside-INCLUDE produces trace with all three frames. |
| Trace iteration index is correct | REPT with 3 iterations produces nodes with iteration 0, 1, 2 in trace. |
| `LoopControl.Break` propagates correctly | BREAK inside nested IF inside REPT exits loop, not outer expansion. |
| Replay depth limit fires independently of structural depth | Deep text replay hits `MaxReplayDepth` while structural depth is low. |
| Structural depth limit fires independently of replay depth | Deep macro nesting hits `MaxStructuralDepth` while replay depth is zero. |
| SHIFT path uses replay only when needed | Macro with SHIFT and deferred params works correctly. Trace shows replay when replay occurs. |
| INCLUDE inside macro/REPT preserves SourceFilePath while trace preserves ancestry | Validates `SourceFilePath` = immediate container, trace = full history. |

### Replay verification approach

Tests construct REPT/FOR bodies known to be structural-eligible, expand them, and assert output nodes' `Trace` does not contain a `TextReplay` frame. If text replay were used, the trace would include a `TextReplay` frame from `ParseForReplay`.

---

## Binder contract

The binder's external contract changes minimally:

- `ExpandedNode.Origin` → `ExpandedNode.Trace`
- `ExpandedNode.SourceFilePath` — unchanged
- `ExpandedNode.FromMacroBody` — unchanged
- `ExpandedNode.WasInConditional` — unchanged
- Flat `List<ExpandedNode>` — unchanged

For backward compatibility, `Trace?.Current?.Kind` replaces `Origin?.Kind`. But the intended future usage is richer: `FindNearest(kind)`, `ContainsKind(kind)`, full ancestry enumeration. Consumers should migrate toward trace inspection, not ossify around the shallow replacement.

---

## File Organization

### New files

| File | Location | Contents |
|------|----------|----------|
| `ExpansionContext.cs` | `Koh.Core/Binding/` | `ExpansionContext` record, `LoopControl` enum |
| `ExpansionTrace.cs` | `Koh.Core/Binding/` | `ExpansionTrace`, `ExpansionFrame`, `ExpansionKind`, `TextReplayReason` |
| `TextReplayService.cs` | `Koh.Core/Binding/` | Text substitution, reparse, classification |
| `BodyReplayPlan.cs` | `Koh.Core/Binding/` | `BodyReplayKind`, `BodyReplayPlan` |

### Modified files

| File | Change |
|------|--------|
| `AssemblyExpander.cs` | All method signatures gain `ctx`. Ambient fields removed. Text methods extracted. Structured replay paths added. |
| `Binder.cs` | `Origin` → `Trace` at read sites. |

### Deleted files

| File | Reason |
|------|--------|
| `ExpansionOrigin.cs` | Replaced by `ExpansionTrace` + `ExpansionFrame` |

---

## What this pass does NOT do

- Does not replace the flat `List<ExpandedNode>` binder contract with a richer `ExpandedCompilation` model.
- Does not attempt full structural handling of concatenative macro/token synthesis.
- Does not rewrite binder diagnostics to use expansion traces (though it produces the data that makes that possible).
- Does not extract `_macros` into a separate `MacroRepository` (identified as next-step candidate).
- Does not make `MacroFrame` immutable (SHIFT pragmatic compromise).
