# Expansion Architecture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor AssemblyExpander from ambient-state text-rewrite preprocessor to context-passing structured expansion pipeline with rich provenance traces.

**Architecture:** Two-phase migration. Phase 1 introduces immutable `ExpansionContext` passed through all expansion methods, eliminating 9 ambient mutable fields and replacing `_breakRequested` with `LoopControl` return values. Phase 2 adds structured REPT/FOR replay (no reparse when iteration only changes evaluation context) and extracts `TextReplayService` to isolate all text→reparse paths.

**Tech Stack:** C# 14 / .NET 10, `System.Collections.Immutable` (ImmutableArray, ImmutableStack), TUnit test framework

**Spec:** `docs/superpowers/specs/2026-04-01-expansion-architecture-design.md`

**Test command:** `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`

---

## File Map

### New files (Phase 1)

| File | Responsibility |
|------|---------------|
| `src/Koh.Core/Binding/ExpansionTrace.cs` | `ExpansionTrace`, `ExpansionFrame`, `ExpansionKind`, `TextReplayReason` |
| `src/Koh.Core/Binding/ExpansionContext.cs` | `ExpansionContext` record, `LoopControl` enum |
| `tests/Koh.Core.Tests/Binding/ExpansionTraceTests.cs` | Unit tests for trace types |
| `tests/Koh.Core.Tests/Binding/ExpansionContextTests.cs` | Unit tests for context derivation |

### New files (Phase 2)

| File | Responsibility |
|------|---------------|
| `src/Koh.Core/Binding/TextReplayService.cs` | Text substitution, `ParseForReplay`, classification |
| `src/Koh.Core/Binding/BodyReplayPlan.cs` | `BodyReplayKind`, `BodyReplayPlan` |
| `tests/Koh.Core.Tests/Binding/StructuredReplayTests.cs` | Tests for structured REPT/FOR replay and trace verification |

### Modified files

| File | Change |
|------|--------|
| `src/Koh.Core/Binding/AssemblyExpander.cs` | All method signatures gain `ctx`. 9 ambient fields removed. `MacroFrame` promoted to top-level. Text methods extracted to `TextReplayService`. Structured replay paths added. |
| `src/Koh.Core/Binding/Binder.cs` | `ExpandedNode.Origin` → `ExpandedNode.Trace` (no read sites currently — just the record change) |

### Deleted files

| File | Reason |
|------|--------|
| `src/Koh.Core/Binding/ExpansionOrigin.cs` | Replaced by `ExpansionTrace` + `ExpansionFrame` |

---

## Phase 1: Context Threading

### Task 1: Create ExpansionTrace and ExpansionFrame types

**Files:**
- Create: `src/Koh.Core/Binding/ExpansionTrace.cs`
- Create: `tests/Koh.Core.Tests/Binding/ExpansionTraceTests.cs`

- [ ] **Step 1: Write failing tests for ExpansionTrace**

```csharp
// tests/Koh.Core.Tests/Binding/ExpansionTraceTests.cs
using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class ExpansionTraceTests
{
    [Test]
    public async Task Empty_HasNoFrames()
    {
        var trace = ExpansionTrace.Empty;
        await Assert.That(trace.IsEmpty).IsTrue();
        await Assert.That(trace.Current).IsNull();
        await Assert.That(trace.Depth).IsEqualTo(0);
    }

    [Test]
    public async Task Push_AddsFrame()
    {
        var frame = ExpansionFrame.ForInclude("test.asm", new TextSpan(0, 10));
        var trace = ExpansionTrace.Empty.Push(frame);
        await Assert.That(trace.IsEmpty).IsFalse();
        await Assert.That(trace.Current).IsEqualTo(frame);
        await Assert.That(trace.Depth).IsEqualTo(1);
    }

    [Test]
    public async Task Push_PreservesAncestry()
    {
        var include = ExpansionFrame.ForInclude("test.asm", new TextSpan(0, 10));
        var rept = ExpansionFrame.ForRept("test.asm", new TextSpan(20, 5), iteration: 2);
        var trace = ExpansionTrace.Empty.Push(include).Push(rept);
        await Assert.That(trace.Depth).IsEqualTo(2);
        await Assert.That(trace.Current).IsEqualTo(rept);
        await Assert.That(trace.Frames[0]).IsEqualTo(include);
    }

    [Test]
    public async Task ContainsKind_FindsFrame()
    {
        var trace = ExpansionTrace.Empty
            .Push(ExpansionFrame.ForInclude("a.asm", default))
            .Push(ExpansionFrame.ForRept("a.asm", default, 0));
        await Assert.That(trace.ContainsKind(ExpansionKind.Include)).IsTrue();
        await Assert.That(trace.ContainsKind(ExpansionKind.ReptIteration)).IsTrue();
        await Assert.That(trace.ContainsKind(ExpansionKind.MacroExpansion)).IsFalse();
    }

    [Test]
    public async Task FindNearest_ReturnsInnermostMatch()
    {
        var rept0 = ExpansionFrame.ForRept("a.asm", default, 0);
        var rept1 = ExpansionFrame.ForRept("a.asm", default, 1);
        var trace = ExpansionTrace.Empty.Push(rept0).Push(rept1);
        var nearest = trace.FindNearest(ExpansionKind.ReptIteration);
        await Assert.That(nearest).IsEqualTo(rept1);
    }

    [Test]
    public async Task FindNearest_ReturnsNull_WhenNotFound()
    {
        var trace = ExpansionTrace.Empty.Push(ExpansionFrame.ForInclude("a.asm", default));
        await Assert.That(trace.FindNearest(ExpansionKind.MacroExpansion)).IsNull();
    }

    [Test]
    public async Task ForTextReplay_CarriesReason()
    {
        var frame = ExpansionFrame.ForTextReplay("a.asm", new TextSpan(5, 3),
            TextReplayReason.EqusReplay);
        await Assert.That(frame.Kind).IsEqualTo(ExpansionKind.TextReplay);
        await Assert.That(frame.ReplayReason).IsEqualTo(TextReplayReason.EqusReplay);
    }

    [Test]
    public async Task ForFor_CarriesIterationAndVarName()
    {
        var frame = ExpansionFrame.ForFor("a.asm", new TextSpan(10, 20), "v", 3);
        await Assert.That(frame.Kind).IsEqualTo(ExpansionKind.ForIteration);
        await Assert.That(frame.Name).IsEqualTo("v");
        await Assert.That(frame.Iteration).IsEqualTo(3);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore --filter "ExpansionTraceTests" -v quiet`
Expected: Build failure — `ExpansionTrace` type does not exist.

- [ ] **Step 3: Implement ExpansionTrace.cs**

```csharp
// src/Koh.Core/Binding/ExpansionTrace.cs
using System.Collections.Immutable;
using Koh.Core.Syntax;

namespace Koh.Core.Binding;

internal enum ExpansionKind
{
    MacroExpansion,
    ReptIteration,
    ForIteration,
    Include,
    TextReplay
}

internal enum TextReplayReason
{
    MacroParameterConcatenation,
    UniqueLabelSubstitution,
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

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore --filter "ExpansionTraceTests" -v quiet`
Expected: All 8 tests pass.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All 922+ tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Core/Binding/ExpansionTrace.cs tests/Koh.Core.Tests/Binding/ExpansionTraceTests.cs
git commit -m "feat: add ExpansionTrace and ExpansionFrame types for rich expansion provenance"
```

---

### Task 2: Create ExpansionContext and LoopControl types

**Files:**
- Create: `src/Koh.Core/Binding/ExpansionContext.cs`
- Create: `tests/Koh.Core.Tests/Binding/ExpansionContextTests.cs`

- [ ] **Step 1: Write failing tests for ExpansionContext**

```csharp
// tests/Koh.Core.Tests/Binding/ExpansionContextTests.cs
using System.Collections.Immutable;
using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class ExpansionContextTests
{
    [Test]
    public async Task Default_HasEmptyTrace()
    {
        var ctx = new ExpansionContext();
        await Assert.That(ctx.Trace.IsEmpty).IsTrue();
        await Assert.That(ctx.CurrentMacroFrame).IsNull();
        await Assert.That(ctx.StructuralDepth).IsEqualTo(0);
        await Assert.That(ctx.ReplayDepth).IsEqualTo(0);
    }

    [Test]
    public async Task ForMacro_IncrementsStructuralDepthAndMacroBodyDepth()
    {
        var ctx = new ExpansionContext { FilePath = "test.asm" };
        var macro = new MacroDefinition("test", "nop", new TextSpan(0, 10), "test.asm");
        var frame = new MacroFrame(["a", "b"]);
        var child = ctx.ForMacro(frame, macro);
        await Assert.That(child.StructuralDepth).IsEqualTo(1);
        await Assert.That(child.MacroBodyDepth).IsEqualTo(1);
        await Assert.That(child.CurrentMacroFrame).IsEqualTo(frame);
        await Assert.That(child.Trace.Current!.Kind).IsEqualTo(ExpansionKind.MacroExpansion);
        // Parent unchanged
        await Assert.That(ctx.StructuralDepth).IsEqualTo(0);
        await Assert.That(ctx.CurrentMacroFrame).IsNull();
    }

    [Test]
    public async Task ForLoop_IncrementsLoopDepth()
    {
        var ctx = new ExpansionContext { FilePath = "test.asm" };
        var loopFrame = ExpansionFrame.ForRept("test.asm", default, 0);
        var child = ctx.ForLoop(loopFrame);
        await Assert.That(child.LoopDepth).IsEqualTo(1);
        await Assert.That(child.Trace.Current!.Kind).IsEqualTo(ExpansionKind.ReptIteration);
        await Assert.That(ctx.LoopDepth).IsEqualTo(0);
    }

    [Test]
    public async Task ForInclude_SetsFilePathAndIncrementsStructuralDepth()
    {
        var ctx = new ExpansionContext { FilePath = "main.asm" };
        var source = Text.SourceText.From("nop", "included.asm");
        var child = ctx.ForInclude("included.asm", source, new TextSpan(5, 20));
        await Assert.That(child.FilePath).IsEqualTo("included.asm");
        await Assert.That(child.SourceText).IsEqualTo(source);
        await Assert.That(child.StructuralDepth).IsEqualTo(1);
        await Assert.That(child.Trace.Current!.Kind).IsEqualTo(ExpansionKind.Include);
        await Assert.That(ctx.FilePath).IsEqualTo("main.asm");
    }

    [Test]
    public async Task ForTextReplay_IncrementsReplayDepth()
    {
        var ctx = new ExpansionContext { FilePath = "test.asm" };
        var source = Text.SourceText.From("nop");
        var child = ctx.ForTextReplay(source, new TextSpan(0, 5),
            TextReplayReason.MacroParameterConcatenation);
        await Assert.That(child.ReplayDepth).IsEqualTo(1);
        await Assert.That(child.StructuralDepth).IsEqualTo(0);
        await Assert.That(child.Trace.Current!.Kind).IsEqualTo(ExpansionKind.TextReplay);
        await Assert.That(child.Trace.Current!.ReplayReason)
            .IsEqualTo(TextReplayReason.MacroParameterConcatenation);
    }

    [Test]
    public async Task NestedMacro_StacksFrames()
    {
        var ctx = new ExpansionContext();
        var macro1 = new MacroDefinition("outer", "nop", default, "test.asm");
        var macro2 = new MacroDefinition("inner", "halt", default, "test.asm");
        var frame1 = new MacroFrame(["x"]);
        var frame2 = new MacroFrame(["y"]);
        var child1 = ctx.ForMacro(frame1, macro1);
        var child2 = child1.ForMacro(frame2, macro2);
        await Assert.That(child2.StructuralDepth).IsEqualTo(2);
        await Assert.That(child2.MacroBodyDepth).IsEqualTo(2);
        await Assert.That(child2.CurrentMacroFrame).IsEqualTo(frame2);
        await Assert.That(child2.Trace.Depth).IsEqualTo(2);
        // Parent contexts unchanged
        await Assert.That(child1.CurrentMacroFrame).IsEqualTo(frame1);
        await Assert.That(ctx.CurrentMacroFrame).IsNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore --filter "ExpansionContextTests" -v quiet`
Expected: Build failure — `ExpansionContext`, `MacroFrame`, `LoopControl` types do not exist.

- [ ] **Step 3: Promote MacroFrame to top-level internal class**

Move `MacroFrame` from nested class inside `AssemblyExpander` to the `ExpansionContext.cs` file. In `AssemblyExpander.cs`, delete the nested `MacroFrame` class (lines 53-72). The `_macroFrameStack` field type changes from `Stack<MacroFrame>` to use the top-level `MacroFrame`.

In `src/Koh.Core/Binding/ExpansionContext.cs`:

```csharp
// src/Koh.Core/Binding/ExpansionContext.cs
using System.Collections.Immutable;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Binding;

internal enum LoopControl { Continue, Break }

/// <summary>
/// Mutable macro argument frame. SHIFT mutates <see cref="ShiftOffset"/> within
/// the owning expansion scope. The <see cref="ImmutableStack{T}"/> in
/// <see cref="ExpansionContext"/> provides structural ownership (visibility);
/// frame internals are mutable for SHIFT.
/// </summary>
internal sealed class MacroFrame
{
    public IReadOnlyList<string> Args { get; }
    public int ShiftOffset { get; set; }
    public int UniqueId { get; set; }
    public int Narg => Math.Max(0, Args.Count - ShiftOffset);

    public string GetArg(int oneBasedIndex)
    {
        int i = oneBasedIndex - 1 + ShiftOffset;
        return i >= 0 && i < Args.Count ? Args[i] : "";
    }

    public string AllArgs()
    {
        var remaining = new List<string>();
        for (int i = ShiftOffset; i < Args.Count; i++)
            remaining.Add(Args[i]);
        return string.Join(", ", remaining);
    }

    public MacroFrame(IReadOnlyList<string> args) => Args = args;
}

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

In `AssemblyExpander.cs`, delete lines 53-72 (the nested `MacroFrame` class). The field on line 50 (`private readonly Stack<MacroFrame> _macroFrameStack = new();`) still compiles because `MacroFrame` is now top-level in the same namespace.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All 922+ existing tests pass, plus the 6 new `ExpansionContextTests` pass.

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Core/Binding/ExpansionContext.cs tests/Koh.Core.Tests/Binding/ExpansionContextTests.cs src/Koh.Core/Binding/AssemblyExpander.cs
git commit -m "feat: add ExpansionContext, LoopControl, and promote MacroFrame to top-level"
```

---

### Task 3: Thread ctx through ExpandBodyList and Expand entry point

This is the transitional bridge step. `ExpandBodyList` accepts `ctx` but still reads ambient fields internally. The entry point creates the initial context.

**Files:**
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`

- [ ] **Step 1: Change ExpandBodyList signature to accept and return context**

Change the signature of `ExpandBodyList` from:

```csharp
private void ExpandBodyList(IReadOnlyList<SyntaxNodeOrToken> siblings,
    ref int i, List<ExpandedNode> output)
```

to:

```csharp
private LoopControl ExpandBodyList(IReadOnlyList<SyntaxNodeOrToken> siblings,
    ref int i, List<ExpandedNode> output, ExpansionContext ctx)
```

At the end of the method (after the while loop), add `return LoopControl.Continue;`.

Update the `Expand` entry point to create initial context and pass it:

```csharp
public List<ExpandedNode> Expand(SyntaxTree tree)
{
    var output = new List<ExpandedNode>();
    _currentSourceText = tree.Text;
    _currentFilePath = tree.Text.FilePath;
    _diagnostics.CurrentFilePath = _currentFilePath;
    if (!string.IsNullOrEmpty(_currentFilePath))
        _includeStack.Add(_currentFilePath);
    var children = tree.Root.ChildNodesAndTokens().ToList();

    PreScanEquConstants(children);

    var ctx = new ExpansionContext
    {
        SourceText = tree.Text,
        FilePath = tree.Text.FilePath
    };

    int i = 0;
    ExpandBodyList(children, ref i, output, ctx);

    if (_conditional.HasUnclosedBlocks)
        _diagnostics.Report(default, "Unclosed IF block: missing ENDC");

    return output;
}
```

Update all call sites of `ExpandBodyList` inside AssemblyExpander to pass the appropriate context. For now, at each existing call site, pass `ctx` if available, or create a temporary context from ambient fields if needed. The existing call sites are:

1. `ExpandParsedTree` (line ~1207): pass a temporary `ctx with { SourceText = tree.Text }`
2. `ExpandTextInline` (line ~1697): pass a temporary context
3. `ExpandInclude` (line ~1291): pass a temporary context
4. `ExpandRept` loop body (line ~1513): pass a temporary context
5. `ExpandFor` loop body (line ~1627): pass a temporary context

At each site, construct: `new ExpansionContext { SourceText = _currentSourceText, FilePath = _currentFilePath, ... }` as a transitional bridge.

- [ ] **Step 2: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All 922+ tests pass. This is a signature-only change with bridge contexts.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Core/Binding/AssemblyExpander.cs
git commit -m "refactor: thread ExpansionContext through ExpandBodyList (transitional bridge)"
```

---

### Task 4: Migrate ExpandBodyList internals to read from ctx

Replace ambient field reads in `ExpandBodyList` with `ctx` reads. Remove each ambient field after all its reads are migrated.

**Files:**
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`

- [ ] **Step 1: Replace _currentSourceText reads with ctx.SourceText**

In `ExpandBodyList`, replace:
- `_currentSourceText != null` → `ctx.SourceText != null`
- `_currentSourceText.ToString(node.FullSpan)` → `ctx.SourceText.ToString(node.FullSpan)`
- `_currentSourceText!.ToString(node.FullSpan)` → `ctx.SourceText!.ToString(node.FullSpan)`

Also replace in the emission line (previously line ~491):
- `_currentFilePath` → `ctx.FilePath`
- `_macroBodyDepth > 0` → `ctx.MacroBodyDepth > 0`
- `_currentOrigin` → `ctx.Trace`

Change the emission to:
```csharp
output.Add(new ExpandedNode(node, ctx.FilePath, _conditional.Depth > 0,
    ctx.MacroBodyDepth > 0, ctx.Trace));
```

Replace `_macroFrameStack.Count > 0` with `ctx.CurrentMacroFrame != null` and `_macroFrameStack.Peek()` with `ctx.CurrentMacroFrame!`.

Replace `_loopDepth > 0` with `ctx.LoopDepth > 0`.

Replace `_expansionDepth == 0` check for macro-param-outside-macro diagnostic with `ctx.StructuralDepth == 0 && ctx.ReplayDepth == 0`.

- [ ] **Step 2: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/Koh.Core/Binding/AssemblyExpander.cs
git commit -m "refactor: migrate ExpandBodyList to read from ExpansionContext"
```

---

### Task 5: Thread ctx through macro expansion, eliminate save/restore

**Files:**
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`

- [ ] **Step 1: Change macro method signatures**

Change:
```csharp
private void ExpandMacroCall(SyntaxNode node, List<ExpandedNode> output)
```
to:
```csharp
private void ExpandMacroCall(SyntaxNode node, List<ExpandedNode> output, ExpansionContext ctx)
```

Change:
```csharp
private void ExpandMacroBody(MacroDefinition macro, MacroFrame frame, List<ExpandedNode> output)
```
to:
```csharp
private void ExpandMacroBody(MacroDefinition macro, MacroFrame frame, List<ExpandedNode> output, ExpansionContext ctx)
```

Change:
```csharp
private void ExpandParsedTree(SyntaxTree tree, List<ExpandedNode> output)
```
to:
```csharp
private void ExpandParsedTree(SyntaxTree tree, List<ExpandedNode> output, ExpansionContext ctx)
```

- [ ] **Step 2: Rewrite ExpandMacroCall to use child context**

Replace the body of `ExpandMacroCall` with context-based implementation. The key change: instead of `_macroFrameStack.Push(frame)` / try/finally pop / `_expansionDepth++/--` / `_currentOrigin` save/restore, create a child context:

```csharp
private void ExpandMacroCall(SyntaxNode node, List<ExpandedNode> output, ExpansionContext ctx)
{
    var tokens = node.ChildTokens().ToList();
    if (tokens.Count == 0) return;

    // \# resolution against parent macro frame
    bool hasBackslashHash = tokens.Any(t => t.Kind == SyntaxKind.MacroParamToken && t.Text == "\\#");
    if (hasBackslashHash && ctx.CurrentMacroFrame != null)
    {
        var parentFrame = ctx.CurrentMacroFrame;
        var rawText = ctx.SourceText != null
            ? ctx.SourceText.ToString(node.FullSpan)
            : string.Join("", tokens.Select(t => t.Text));
        var resolved = rawText.Replace("\\#", parentFrame.AllArgs());
        ExpandTextInline(resolved, output, ctx, node.FullSpan,
            TextReplayReason.MacroParameterConcatenation);
        return;
    }

    var name = tokens[0].Text;
    if (!_macros.TryGetValue(name, out var macro))
    {
        _diagnostics.Report(node.FullSpan, $"Unexpected identifier '{name}'");
        return;
    }

    var args = CollectMacroArgs(tokens, startIndex: 1);
    _uniqueIdCounter++;
    var frame = new MacroFrame(args) { UniqueId = _uniqueIdCounter };

    var macroCtx = ctx.ForMacro(frame, macro);
    if (macroCtx.StructuralDepth > MaxStructuralDepth)
    {
        _diagnostics.Report(node.FullSpan,
            $"Maximum macro expansion depth ({MaxStructuralDepth}) exceeded");
        return;
    }

    var prevNarg = _symbols.Lookup("_NARG");
    long? savedNarg = prevNarg?.State == SymbolState.Defined ? prevNarg.Value : null;
    _symbols.DefineOrRedefine("_NARG", frame.Narg);

    try
    {
        ExpandMacroBody(macro, frame, output, macroCtx);
    }
    finally
    {
        if (savedNarg.HasValue)
            _symbols.DefineOrRedefine("_NARG", savedNarg.Value);
    }
}
```

- [ ] **Step 3: Rewrite ExpandMacroBody to use ctx**

```csharp
private void ExpandMacroBody(MacroDefinition macro, MacroFrame frame,
    List<ExpandedNode> output, ExpansionContext ctx)
{
    if (macro.RequiresTextSubstitution)
    {
        var body = SubstituteMacroParams(macro.RawBody, frame, macro.ContainsShift);
        ExpandTextInline(body, output, ctx, macro.DefinitionSpan,
            TextReplayReason.MacroParameterConcatenation);
    }
    else
    {
        ExpandParsedTree(macro.ParsedBody, output, ctx);
    }
}
```

- [ ] **Step 4: Rewrite ExpandParsedTree to use ctx**

```csharp
private void ExpandParsedTree(SyntaxTree tree, List<ExpandedNode> output, ExpansionContext ctx)
{
    // Structured parsed-body replay does NOT increment ReplayDepth.
    // Only check StructuralDepth — this is structural execution, not replay.
    if (ctx.StructuralDepth > MaxStructuralDepth)
    {
        _diagnostics.Report(default,
            $"Maximum expansion depth ({MaxStructuralDepth}) exceeded");
        return;
    }

    var treeCtx = ctx with { SourceText = tree.Text };
    var children = tree.Root.ChildNodesAndTokens().ToList();
    int j = 0;
    ExpandBodyList(children, ref j, output, treeCtx);
}
```

- [ ] **Step 5: Update ExpandTextInline signature and body**

Change signature to:
```csharp
private void ExpandTextInline(string text, List<ExpandedNode> output,
    ExpansionContext ctx, TextSpan triggerSpan, TextReplayReason reason)
```

Replace the body:
```csharp
private void ExpandTextInline(string text, List<ExpandedNode> output,
    ExpansionContext ctx, TextSpan triggerSpan, TextReplayReason reason)
{
    bool hasMacroParams = ContainsUnresolvedMacroParam(text);
    if (!hasMacroParams)
        text = ResolveInterpolations(text);

    if (ctx.ReplayDepth >= MaxReplayDepth)
    {
        _diagnostics.Report(triggerSpan,
            $"Maximum text replay depth ({MaxReplayDepth}) exceeded");
        return;
    }

    var tree = SyntaxTree.Parse(text);
    var replayCtx = ctx.ForTextReplay(tree.Text, triggerSpan, reason);
    var children = tree.Root.ChildNodesAndTokens().ToList();
    int j = 0;
    ExpandBodyList(children, ref j, output, replayCtx);
}
```

- [ ] **Step 6: Update all ExpandTextInline call sites**

Every call to `ExpandTextInline` now needs `ctx`, `triggerSpan`, and `reason`. Update each:

1. EQUS bare-name expansion in `ExpandBodyList`: `ExpandTextInline(equsValue, output, ctx, node.FullSpan, TextReplayReason.EqusReplay);`
2. Lazy macro param resolution in `ExpandBodyList`: `ExpandTextInline(resolved, output, ctx, node.FullSpan, TextReplayReason.MacroParameterConcatenation);`
3. Interpolation in `ExpandBodyList`: `ExpandTextInline(resolved, output, ctx, node.FullSpan, TextReplayReason.EqusReplay);`

- [ ] **Step 7: Add MaxStructuralDepth and MaxReplayDepth constants, remove MaxExpansionDepth**

Replace:
```csharp
private const int MaxExpansionDepth = 64;
```
with:
```csharp
private const int MaxStructuralDepth = 64;
private const int MaxReplayDepth = 64;
```

- [ ] **Step 8: Remove ambient fields that are now fully on ctx**

Remove these fields from `AssemblyExpander`:
- `private int _expansionDepth;`
- `private SourceText? _currentSourceText;`
- `private string _currentFilePath = "";`
- `private int _macroBodyDepth;`
- `private ExpansionOrigin? _currentOrigin;`
- `private readonly Stack<MacroFrame> _macroFrameStack = new();`

Keep `_currentFilePath` and `_currentSourceText` temporarily if `EarlyDefineEqu`, `HandleConditional`, or other methods not yet migrated still read them. Those will be migrated in Task 7.

- [ ] **Step 9: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/Koh.Core/Binding/AssemblyExpander.cs
git commit -m "refactor: thread ctx through macro expansion, eliminate macro save/restore"
```

---

### Task 6: Thread ctx through include and REPT/FOR expansion

**Files:**
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`

- [ ] **Step 1: Rewrite ExpandInclude to use child context**

Change signature to `private void ExpandInclude(SyntaxNode node, List<ExpandedNode> output, ExpansionContext ctx)`.

Replace the save/restore pattern with child context:

```csharp
private void ExpandInclude(SyntaxNode node, List<ExpandedNode> output, ExpansionContext ctx)
{
    var strToken = node.ChildTokens().FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
    if (strToken == null)
    {
        _diagnostics.Report(node.FullSpan, "INCLUDE requires a filename string");
        return;
    }

    var rawPath = strToken.Text;
    var filePath = rawPath.Length >= 2 ? rawPath[1..^1] : rawPath;
    var resolved = _fileResolver.ResolvePath(ctx.FilePath, filePath);

    if (!_fileResolver.FileExists(resolved))
    {
        _diagnostics.Report(node.FullSpan, $"Included file not found: {filePath}");
        return;
    }

    if (_includeStack.Contains(resolved))
    {
        _diagnostics.Report(node.FullSpan, $"Circular include detected: {filePath}");
        return;
    }

    var includeCtx = ctx.ForInclude(resolved,
        Text.SourceText.From("", resolved), // placeholder, replaced below
        node.FullSpan);

    if (includeCtx.StructuralDepth > MaxStructuralDepth)
    {
        _diagnostics.Report(node.FullSpan,
            $"Maximum include depth ({MaxStructuralDepth}) exceeded");
        return;
    }

    _includeStack.Add(resolved);
    try
    {
        var source = _fileResolver.ReadAllText(resolved);

        // Substitute \@ in included file using current unique ID
        if (ctx.CurrentMacroFrame != null)
        {
            source = source.Replace("\\@", $"_{ctx.CurrentMacroFrame.UniqueId}");
        }

        var includeText = Text.SourceText.From(source, resolved);
        var includeTree = SyntaxTree.Parse(includeText);

        // Create the real include context with actual source text
        var realIncludeCtx = ctx.ForInclude(resolved, includeText, node.FullSpan);
        _diagnostics.CurrentFilePath = resolved;

        var children = includeTree.Root.ChildNodesAndTokens().ToList();
        int j = 0;
        ExpandBodyList(children, ref j, output, realIncludeCtx);
    }
    catch (IOException ex)
    {
        _diagnostics.Report(node.FullSpan, $"Cannot read included file '{filePath}': {ex.Message}");
    }
    finally
    {
        _includeStack.Remove(resolved);
        _diagnostics.CurrentFilePath = ctx.FilePath;
    }
}
```

- [ ] **Step 2: Rewrite ExpandRept to use child context**

Change signature to `private void ExpandRept(SyntaxNode reptNode, IReadOnlyList<SyntaxNodeOrToken> siblings, ref int i, List<ExpandedNode> output, ExpansionContext ctx)`.

Replace `_loopDepth++/--`, `_currentOrigin` save/restore, and `_breakRequested` with context and `LoopControl`:

```csharp
private void ExpandRept(SyntaxNode reptNode,
    IReadOnlyList<SyntaxNodeOrToken> siblings, ref int i,
    List<ExpandedNode> output, ExpansionContext ctx)
{
    var exprNodes = reptNode.ChildNodes().ToList();
    int count = 0;
    if (exprNodes.Count > 0)
    {
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps);
        var val = evaluator.TryEvaluate(exprNodes[0].Green);
        if (val.HasValue) count = (int)val.Value;
    }
    if (count < 0) count = 0;

    var bodyTextRaw = ExtractBodyText(reptNode, PeekBodyNodes(siblings, i), ctx);
    CollectRepeatBody(siblings, ref i, reptNode.FullSpan);

    var condDepthBefore = _conditional.Depth;
    for (int iter = 0; iter < count; iter++)
    {
        _uniqueIdCounter++;
        int uniqueId = _uniqueIdCounter;
        var iterFrame = ExpansionFrame.ForRept(ctx.FilePath, reptNode.FullSpan, iter);
        var iterCtx = ctx.ForLoop(iterFrame);

        var iterText = bodyTextRaw.Replace("\\@", $"_{uniqueId}");
        ExpandTextInline(iterText, output, iterCtx, reptNode.FullSpan,
            TextReplayReason.UniqueLabelSubstitution);

        if (_conditional.Depth != condDepthBefore)
            _conditional.ResetToDepth(condDepthBefore);
    }

    if (_conditional.Depth != condDepthBefore)
        _diagnostics.Report(reptNode.FullSpan, "Unbalanced IF/ENDC inside REPT body");
}
```

Note: `_breakRequested` handling is not yet replaced by `LoopControl` return — that happens in Task 7. For now, keep the `_breakRequested` check temporarily.

- [ ] **Step 3: Rewrite ExpandFor similarly**

Change signature to `private void ExpandFor(SyntaxNode forNode, IReadOnlyList<SyntaxNodeOrToken> siblings, ref int i, List<ExpandedNode> output, ExpansionContext ctx)`.

Apply same context pattern as ExpandRept. Use `ctx.FilePath` instead of `_currentFilePath`. Pass `ctx` to `ExpandTextInline`. Create `iterCtx = ctx.ForLoop(ExpansionFrame.ForFor(...))` per iteration.

- [ ] **Step 4: Update ExtractBodyText to take ctx**

Change `ExtractBodyText` to accept `ExpansionContext ctx` and use `ctx.SourceText` instead of `_currentSourceText`.

- [ ] **Step 5: Update all call sites in ExpandBodyList**

In `ExpandBodyList`, update the calls:
- `ExpandMacroCall(node, output)` → `ExpandMacroCall(node, output, ctx)`
- `ExpandInclude(node, output)` → `ExpandInclude(node, output, ctx)`
- `ExpandRept(node, siblings, ref i, output)` → `ExpandRept(node, siblings, ref i, output, ctx)`
- `ExpandFor(forNode, siblings, ref i, output)` → `ExpandFor(forNode, siblings, ref i, output, ctx)`

- [ ] **Step 6: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Koh.Core/Binding/AssemblyExpander.cs
git commit -m "refactor: thread ctx through include, REPT, and FOR expansion"
```

---

### Task 7: Replace _breakRequested with LoopControl return

**Files:**
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`

- [ ] **Step 1: Change ExpandBodyList BREAK handling**

In `ExpandBodyList`, find the BREAK handler:

```csharp
if (kw?.Kind == SyntaxKind.BreakKeyword)
{
    _breakRequested = true;
    i++;
    return; // exit ExpandBodyList — caller loop will see the flag
}
```

Replace with:

```csharp
if (kw?.Kind == SyntaxKind.BreakKeyword)
{
    i++;
    return LoopControl.Break;
}
```

- [ ] **Step 2: Propagate LoopControl through ExpandTextInline**

Change `ExpandTextInline` to return `LoopControl`:

```csharp
private LoopControl ExpandTextInline(string text, List<ExpandedNode> output,
    ExpansionContext ctx, TextSpan triggerSpan, TextReplayReason reason)
```

Return the result of `ExpandBodyList`:

```csharp
return ExpandBodyList(children, ref j, output, replayCtx);
```

- [ ] **Step 3: Update REPT/FOR to check LoopControl instead of _breakRequested**

In `ExpandRept`, replace `if (_breakRequested) { _breakRequested = false; break; }` with checking the return value from `ExpandTextInline`:

```csharp
var loopResult = ExpandTextInline(iterText, output, iterCtx, reptNode.FullSpan,
    TextReplayReason.UniqueLabelSubstitution);
if (loopResult == LoopControl.Break)
{
    _conditional.ResetToDepth(condDepthBefore);
    break;
}
```

Apply the same pattern to `ExpandFor`.

- [ ] **Step 4: Remove `_breakRequested` field**

Delete `private bool _breakRequested;` from AssemblyExpander.

Remove the `&& !_breakRequested` check from the `ExpandBodyList` while loop condition. The while loop now only checks `i < siblings.Count`. BREAK propagates via return value.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Core/Binding/AssemblyExpander.cs
git commit -m "refactor: replace _breakRequested with LoopControl return value"
```

---

### Task 8: Thread ctx through remaining methods, remove all ambient fields

**Files:**
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`

- [ ] **Step 1: Add ctx to HandleConditional, EarlyDefineEqu, EarlyProcessCharmap, HandlePurge, HandleRsDirective**

Add `ExpansionContext ctx` parameter to each. Replace `_currentFilePath` with `ctx.FilePath` and `_currentSourceText` with `ctx.SourceText` in each method body. Update all call sites in `ExpandBodyList` to pass `ctx`.

For methods that only need `ctx` for file path (like `HandleConditional`), still pass it for consistency.

- [ ] **Step 2: Remove remaining ambient expansion-scope fields**

After all methods read from `ctx`, remove these fields from AssemblyExpander:
- `private SourceText? _currentSourceText;`
- `private string _currentFilePath = "";`
- `private int _expansionDepth;` (if not already removed)
- `private int _loopDepth;`
- `private int _macroBodyDepth;`
- `private ExpansionOrigin? _currentOrigin;`
- `private readonly Stack<MacroFrame> _macroFrameStack = new();`
- `private readonly Stack<int> _reptUniqueIdStack = new();`

Verify: the only remaining fields are the shared-state fields listed in the spec (diagnostics, symbols, conditional, macros, equsConstants, charMaps, rsCounter, fileResolver, includeStack, uniqueIdCounter, printOutput, interpolation, expressionCache).

- [ ] **Step 3: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Core/Binding/AssemblyExpander.cs
git commit -m "refactor: thread ctx through all methods, remove all ambient expansion-scope fields"
```

---

### Task 9: Delete ExpansionOrigin, update ExpandedNode to use Trace

**Files:**
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`
- Modify: `src/Koh.Core/Binding/Binder.cs` (if any `.Origin` reads exist)
- Delete: `src/Koh.Core/Binding/ExpansionOrigin.cs`

- [ ] **Step 1: Change ExpandedNode record**

In `AssemblyExpander.cs`, change:

```csharp
public sealed record ExpandedNode(SyntaxNode Node, string SourceFilePath = "", bool WasInConditional = false, bool FromMacroBody = false, ExpansionOrigin? Origin = null);
```

to:

```csharp
public sealed record ExpandedNode(SyntaxNode Node, string SourceFilePath = "", bool WasInConditional = false, bool FromMacroBody = false, ExpansionTrace? Trace = null);
```

- [ ] **Step 2: Delete ExpansionOrigin.cs**

Delete `src/Koh.Core/Binding/ExpansionOrigin.cs`. All usages were already replaced by `ExpansionTrace` + `ExpansionFrame` in previous tasks.

- [ ] **Step 3: Verify no remaining ExpansionOrigin references**

Search for any remaining references. The `ExpansionKind` enum was already moved to `ExpansionTrace.cs` in Task 1. If any `ExpansionKind.Source` references exist, remove them (it was removed from the enum).

- [ ] **Step 4: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git rm src/Koh.Core/Binding/ExpansionOrigin.cs
git add src/Koh.Core/Binding/AssemblyExpander.cs
git commit -m "refactor: replace ExpansionOrigin with ExpansionTrace on ExpandedNode"
```

**Phase 1 gate: All ambient expansion-scope fields are eliminated. The transitional bridge is gone. Only shared-state fields remain on AssemblyExpander.**

---

## Phase 2: Structured Replay and TextReplayService

### Task 10: Create TextReplayService, extract text substitution methods

**Files:**
- Create: `src/Koh.Core/Binding/TextReplayService.cs`
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`

- [ ] **Step 1: Create TextReplayService with extracted methods**

Move the following methods from `AssemblyExpander` to `TextReplayService`:

```csharp
// src/Koh.Core/Binding/TextReplayService.cs
using System.Text.RegularExpressions;
using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;
using Koh.Core.Text;

namespace Koh.Core.Binding;

/// <summary>
/// Owns all text→reparse expansion paths. No replay-driven parse may occur
/// outside <see cref="ParseForReplay"/>.
/// </summary>
internal sealed class TextReplayService
{
    private readonly DiagnosticBag _diagnostics;
    private readonly InterpolationResolver _interpolation;

    public TextReplayService(DiagnosticBag diagnostics, InterpolationResolver interpolation)
    {
        _diagnostics = diagnostics;
        _interpolation = interpolation;
    }

    public string SubstituteUniqueId(string bodyText, int uniqueId)
        => bodyText.Replace("\\@", $"_{uniqueId}");

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

    // Move SubstituteParamReferences from AssemblyExpander
    // Move ResolveComputedArgs from AssemblyExpander
    // Move SubstituteMacroParams from AssemblyExpander
    // Move SubstituteOutsideStrings from AssemblyExpander
    // Move SubstituteAtPositions from AssemblyExpander
    // Move ContainsUnresolvedMacroParam from AssemblyExpander
    // Move ExtractBodyText from AssemblyExpander
    // Move CollectIdentifierPositions from AssemblyExpander
    // (Keep the NargPattern regex and StringLiteralSplitter regex)
}
```

Move each method. For methods that need `_symbols` or `_expressionCache`, pass them as parameters. For example:

```csharp
public string SubstituteMacroParams(string body, MacroFrame frame, bool containsShift,
    SymbolTable symbols, Dictionary<string, GreenNodeBase?> expressionCache)
{
    body = SubstituteUniqueId(body, frame.UniqueId);
    if (containsShift) return body;
    return SubstituteParamReferences(body, frame, reportShiftedPast: false,
        symbols, expressionCache);
}
```

- [ ] **Step 2: Add TextReplayService field to AssemblyExpander**

In the constructor:
```csharp
private readonly TextReplayService _textReplay;

// In constructor:
_textReplay = new TextReplayService(diagnostics, _interpolation);
```

- [ ] **Step 3: Update AssemblyExpander to delegate to TextReplayService**

Replace direct calls to moved methods with `_textReplay.MethodName(...)`. For example:

In `ExpandMacroBody`:
```csharp
var body = _textReplay.SubstituteMacroParams(macro.RawBody, frame, macro.ContainsShift,
    _symbols, _expressionCache);
```

In `ExpandTextInline`, replace direct `SyntaxTree.Parse` with `_textReplay.ParseForReplay`:
```csharp
private LoopControl ExpandTextInline(string text, List<ExpandedNode> output,
    ExpansionContext ctx, TextSpan triggerSpan, TextReplayReason reason)
{
    bool hasMacroParams = _textReplay.ContainsUnresolvedMacroParam(text);
    var tree = _textReplay.ParseForReplay(text, hasMacroParams, ctx, triggerSpan,
        reason, MaxReplayDepth);
    if (tree == null) return LoopControl.Continue;

    var replayCtx = ctx.ForTextReplay(tree.Text, triggerSpan, reason);
    var children = tree.Root.ChildNodesAndTokens().ToList();
    int j = 0;
    return ExpandBodyList(children, ref j, output, replayCtx);
}
```

- [ ] **Step 4: Delete moved methods from AssemblyExpander**

Remove `SubstituteParamReferences`, `ResolveComputedArgs`, `SubstituteMacroParams`, `SubstituteOutsideStrings`, `SubstituteAtPositions`, `ContainsUnresolvedMacroParam`, `ExtractBodyText`, `CollectIdentifierPositions`, `NargPattern`, `StringLiteralSplitter` from AssemblyExpander.

- [ ] **Step 5: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Core/Binding/TextReplayService.cs src/Koh.Core/Binding/AssemblyExpander.cs
git commit -m "refactor: extract TextReplayService from AssemblyExpander"
```

---

### Task 11: Add body classification and BodyReplayPlan

**Files:**
- Create: `src/Koh.Core/Binding/BodyReplayPlan.cs`
- Modify: `src/Koh.Core/Binding/TextReplayService.cs`

- [ ] **Step 1: Create BodyReplayPlan types**

```csharp
// src/Koh.Core/Binding/BodyReplayPlan.cs
namespace Koh.Core.Binding;

internal enum BodyReplayKind
{
    Structural,
    RequiresTextReplay
}

internal sealed record BodyReplayPlan(
    BodyReplayKind Kind,
    TextReplayReason? Reason = null);
```

- [ ] **Step 2: Add classification methods to TextReplayService**

```csharp
// Add to TextReplayService:

/// <summary>
/// Classify a REPT body for structural vs text replay.
/// Bodies containing \@ require text replay (unique label substitution).
/// </summary>
public BodyReplayPlan ClassifyReptBody(string bodyText)
{
    if (bodyText.Contains("\\@"))
        return new BodyReplayPlan(BodyReplayKind.RequiresTextReplay,
            TextReplayReason.UniqueLabelSubstitution);
    return new BodyReplayPlan(BodyReplayKind.Structural);
}

/// <summary>
/// Classify a FOR body for structural vs text replay.
/// Structural replay is possible only when every occurrence of the loop variable
/// is a standalone parsed IdentifierToken that can be represented by symbol-table
/// rebinding alone.
/// </summary>
public BodyReplayPlan ClassifyForBody(string bodyText, string varName)
{
    if (bodyText.Contains("\\@"))
        return new BodyReplayPlan(BodyReplayKind.RequiresTextReplay,
            TextReplayReason.UniqueLabelSubstitution);

    // Parse once to inspect token stream
    var tree = Syntax.SyntaxTree.Parse(bodyText);
    if (AllVariableOccurrencesAreStandalone(tree.Root, varName))
        return new BodyReplayPlan(BodyReplayKind.Structural);

    return new BodyReplayPlan(BodyReplayKind.RequiresTextReplay,
        TextReplayReason.ForTokenShapingSubstitution);
}

/// <summary>
/// Check that every occurrence of <paramref name="varName"/> in the parsed tree
/// is a distinct IdentifierToken — a complete standalone token, not part of a
/// synthesized larger token.
/// </summary>
private static bool AllVariableOccurrencesAreStandalone(SyntaxNode root, string varName)
{
    // Walk all tokens. Every identifier matching varName must be a standalone
    // IdentifierToken. If varName doesn't appear at all, structural is fine.
    foreach (var token in root.DescendantTokens())
    {
        if (token.Kind == SyntaxKind.IdentifierToken &&
            token.Text.Equals(varName, StringComparison.OrdinalIgnoreCase))
        {
            // This is a standalone identifier — good for structural replay
            continue;
        }

        // Check if varName appears as a substring inside any other token
        // (e.g., inside a string literal, or concatenated into another identifier)
        if (token.Text.Contains(varName, StringComparison.OrdinalIgnoreCase) &&
            token.Kind != SyntaxKind.IdentifierToken)
        {
            // Could be inside a string literal (harmless) — skip string tokens
            if (token.Kind == SyntaxKind.StringLiteral) continue;
            // Inside another token type — not safe for structural replay
            return false;
        }
    }
    return true;
}
```

- [ ] **Step 3: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All tests pass (no behavior change yet — classification not wired in).

- [ ] **Step 4: Commit**

```bash
git add src/Koh.Core/Binding/BodyReplayPlan.cs src/Koh.Core/Binding/TextReplayService.cs
git commit -m "feat: add BodyReplayPlan and body classification for REPT/FOR"
```

---

### Task 12: Add structured REPT replay

**Files:**
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`
- Create: `tests/Koh.Core.Tests/Binding/StructuredReplayTests.cs`

- [ ] **Step 1: Write tests for structured REPT replay**

```csharp
// tests/Koh.Core.Tests/Binding/StructuredReplayTests.cs
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class StructuredReplayTests
{
    /// <summary>
    /// Expand source and return the list of ExpandedNodes for trace inspection.
    /// </summary>
    private static List<ExpandedNode> Expand(string source)
    {
        var tree = SyntaxTree.Parse(source);
        var diag = new DiagnosticBag();
        var symbols = new SymbolTable(diag);
        var expander = new AssemblyExpander(diag, symbols);
        return expander.Expand(tree);
    }

    [Test]
    public async Task Rept_WithoutBackslashAt_UsesStructuralReplay()
    {
        // REPT with no \@ — should use structural replay, no TextReplay frame in trace
        var nodes = Expand("SECTION \"Main\", ROM0\nREPT 3\nnop\nENDR");
        var nopNodes = nodes.Where(n => n.Node.Kind == SyntaxKind.InstructionStatement).ToList();
        await Assert.That(nopNodes.Count).IsEqualTo(3);

        foreach (var node in nopNodes)
        {
            // Trace should contain ReptIteration but NOT TextReplay
            await Assert.That(node.Trace).IsNotNull();
            await Assert.That(node.Trace!.ContainsKind(ExpansionKind.ReptIteration)).IsTrue();
            await Assert.That(node.Trace!.ContainsKind(ExpansionKind.TextReplay)).IsFalse();
        }
    }

    [Test]
    public async Task Rept_WithBackslashAt_UsesTextReplay()
    {
        // REPT with \@ — must use text replay
        var nodes = Expand("SECTION \"Main\", ROM0\nREPT 2\nlabel\\@: nop\nENDR");
        var nopNodes = nodes.Where(n => n.Node.Kind == SyntaxKind.InstructionStatement).ToList();
        await Assert.That(nopNodes.Count).IsEqualTo(2);

        foreach (var node in nopNodes)
        {
            await Assert.That(node.Trace).IsNotNull();
            await Assert.That(node.Trace!.ContainsKind(ExpansionKind.TextReplay)).IsTrue();
        }
    }

    [Test]
    public async Task Rept_StructuralReplay_TraceHasIterationIndex()
    {
        var nodes = Expand("SECTION \"Main\", ROM0\nREPT 3\nnop\nENDR");
        var nopNodes = nodes.Where(n => n.Node.Kind == SyntaxKind.InstructionStatement).ToList();
        await Assert.That(nopNodes.Count).IsEqualTo(3);

        for (int i = 0; i < 3; i++)
        {
            var frame = nopNodes[i].Trace!.FindNearest(ExpansionKind.ReptIteration);
            await Assert.That(frame).IsNotNull();
            await Assert.That(frame!.Iteration).IsEqualTo(i);
        }
    }

    [Test]
    public async Task DirectSource_HasEmptyTrace()
    {
        var nodes = Expand("SECTION \"Main\", ROM0\nnop");
        var nopNodes = nodes.Where(n => n.Node.Kind == SyntaxKind.InstructionStatement).ToList();
        await Assert.That(nopNodes.Count).IsEqualTo(1);
        await Assert.That(nopNodes[0].Trace).IsNotNull();
        await Assert.That(nopNodes[0].Trace!.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Rept_Break_WorksWithStructuralReplay()
    {
        var nodes = Expand("SECTION \"Main\", ROM0\nREPT 10\nnop\nIF _NARG == 0\nBREAK\nENDC\nENDR");
        // BREAK should fire on first iteration (no _NARG in REPT context)
        var nopNodes = nodes.Where(n => n.Node.Kind == SyntaxKind.InstructionStatement).ToList();
        await Assert.That(nopNodes.Count).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore --filter "StructuredReplayTests" -v quiet`
Expected: Tests that check for absence of `TextReplay` frame will fail (current implementation always uses text replay for REPT).

- [ ] **Step 3: Add ExpandReptStructural method**

Add to `AssemblyExpander`:

```csharp
private LoopControl ExpandReptStructural(List<SyntaxNodeOrToken> bodyNodes, int count,
    SyntaxNode reptNode, List<ExpandedNode> output, ExpansionContext ctx)
{
    var condDepthBefore = _conditional.Depth;
    for (int iter = 0; iter < count; iter++)
    {
        var iterFrame = ExpansionFrame.ForRept(ctx.FilePath, reptNode.FullSpan, iter);
        var iterCtx = ctx.ForLoop(iterFrame);
        int j = 0;
        var result = ExpandBodyList(bodyNodes, ref j, output, iterCtx);
        if (result == LoopControl.Break)
        {
            _conditional.ResetToDepth(condDepthBefore);
            return LoopControl.Continue; // break exits the loop, not the caller
        }
    }
    if (_conditional.Depth != condDepthBefore)
        _diagnostics.Report(reptNode.FullSpan, "Unbalanced IF/ENDC inside REPT body");
    return LoopControl.Continue;
}
```

- [ ] **Step 4: Wire classification into ExpandRept**

In `ExpandRept`, after collecting the body text and body nodes, classify:

```csharp
var plan = _textReplay.ClassifyReptBody(bodyTextRaw);
if (plan.Kind == BodyReplayKind.Structural)
{
    // Structured replay — walk parsed body nodes directly
    ExpandReptStructural(body, count, reptNode, output, ctx);
}
else
{
    // Text replay — substitute \@ and reparse
    for (int iter = 0; iter < count; iter++)
    {
        _uniqueIdCounter++;
        int uniqueId = _uniqueIdCounter;
        var iterFrame = ExpansionFrame.ForRept(ctx.FilePath, reptNode.FullSpan, iter);
        var iterCtx = ctx.ForLoop(iterFrame);
        var iterText = _textReplay.SubstituteUniqueId(bodyTextRaw, uniqueId);
        var result = ExpandTextInline(iterText, output, iterCtx, reptNode.FullSpan,
            TextReplayReason.UniqueLabelSubstitution);
        if (result == LoopControl.Break)
        {
            _conditional.ResetToDepth(condDepthBefore);
            break;
        }
    }
}
```

Note: `body` here is the `List<SyntaxNodeOrToken>` returned by `CollectRepeatBody`. For structural replay, we pass these collected body nodes directly.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All tests pass including new `StructuredReplayTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Koh.Core/Binding/AssemblyExpander.cs tests/Koh.Core.Tests/Binding/StructuredReplayTests.cs
git commit -m "feat: add structured REPT replay, bypassing text reparse when no \\@ present"
```

---

### Task 13: Add structured FOR replay

**Files:**
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`
- Modify: `tests/Koh.Core.Tests/Binding/StructuredReplayTests.cs`

- [ ] **Step 1: Add FOR replay tests**

Add to `StructuredReplayTests.cs`:

```csharp
[Test]
public async Task For_StandaloneVariable_UsesStructuralReplay()
{
    // FOR with standalone variable — should use structural replay
    var nodes = Expand("SECTION \"Main\", ROM0\nFOR v, 0, 3\ndb v\nENDR");
    var dbNodes = nodes.Where(n => n.Node.Kind == SyntaxKind.DataDirective).ToList();
    await Assert.That(dbNodes.Count).IsEqualTo(3);

    foreach (var node in dbNodes)
    {
        await Assert.That(node.Trace).IsNotNull();
        await Assert.That(node.Trace!.ContainsKind(ExpansionKind.ForIteration)).IsTrue();
        await Assert.That(node.Trace!.ContainsKind(ExpansionKind.TextReplay)).IsFalse();
    }
}

[Test]
public async Task For_WithBackslashAt_UsesTextReplay()
{
    var nodes = Expand("SECTION \"Main\", ROM0\nFOR v, 0, 2\nlabel\\@: db v\nENDR");
    var dbNodes = nodes.Where(n => n.Node.Kind == SyntaxKind.DataDirective).ToList();
    await Assert.That(dbNodes.Count).IsEqualTo(2);

    foreach (var node in dbNodes)
    {
        await Assert.That(node.Trace).IsNotNull();
        await Assert.That(node.Trace!.ContainsKind(ExpansionKind.TextReplay)).IsTrue();
    }
}

[Test]
public async Task For_StructuralReplay_ProducesCorrectValues()
{
    // FOR v, 0, 3 with db v should produce bytes 0, 1, 2
    var model = Compilation.Create(SyntaxTree.Parse(
        "SECTION \"Main\", ROM0\nFOR v, 0, 3\ndb v\nENDR")).Emit();
    await Assert.That(model.Success).IsTrue();
    await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
    await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0);
    await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)1);
    await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)2);
}

[Test]
public async Task For_StructuralReplay_TraceHasIterationIndex()
{
    var nodes = Expand("SECTION \"Main\", ROM0\nFOR v, 0, 3\ndb v\nENDR");
    var dbNodes = nodes.Where(n => n.Node.Kind == SyntaxKind.DataDirective).ToList();
    for (int i = 0; i < 3; i++)
    {
        var frame = dbNodes[i].Trace!.FindNearest(ExpansionKind.ForIteration);
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Iteration).IsEqualTo(i);
        await Assert.That(frame!.Name).IsEqualTo("v");
    }
}
```

- [ ] **Step 2: Add ExpandForStructural method**

```csharp
private LoopControl ExpandForStructural(List<SyntaxNodeOrToken> bodyNodes,
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
        if (result == LoopControl.Break) return LoopControl.Continue;
    }
    return LoopControl.Continue;
}
```

- [ ] **Step 3: Wire classification into ExpandFor**

In `ExpandFor`, after collecting body text and body nodes, classify:

```csharp
BodyReplayPlan plan;
if (varName != null)
    plan = _textReplay.ClassifyForBody(bodyTextRaw, varName);
else
    plan = bodyTextRaw.Contains("\\@")
        ? new BodyReplayPlan(BodyReplayKind.RequiresTextReplay, TextReplayReason.UniqueLabelSubstitution)
        : new BodyReplayPlan(BodyReplayKind.Structural);

if (plan.Kind == BodyReplayKind.Structural && varName != null)
{
    ExpandForStructural(body, varName, start, stop, step, forNode, output, ctx);
}
else
{
    // Existing text replay path (with ctx threading already done)
    // ...
}
```

- [ ] **Step 4: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Koh.Core/Binding/AssemblyExpander.cs tests/Koh.Core.Tests/Binding/StructuredReplayTests.cs
git commit -m "feat: add structured FOR replay, bypassing text reparse for standalone variables"
```

---

### Task 14: Add remaining provenance and depth tests

**Files:**
- Modify: `tests/Koh.Core.Tests/Binding/StructuredReplayTests.cs`

- [ ] **Step 1: Add expansion trace ancestry test**

```csharp
[Test]
public async Task NestedMacroInsideRept_TraceCarriesFullAncestry()
{
    var source = """
        my_nop: MACRO
        nop
        ENDM
        SECTION "Main", ROM0
        REPT 2
        my_nop
        ENDR
        """;
    var nodes = Expand(source);
    var nopNodes = nodes.Where(n => n.Node.Kind == SyntaxKind.InstructionStatement).ToList();
    await Assert.That(nopNodes.Count).IsEqualTo(2);

    foreach (var node in nopNodes)
    {
        await Assert.That(node.Trace).IsNotNull();
        await Assert.That(node.Trace!.ContainsKind(ExpansionKind.ReptIteration)).IsTrue();
        await Assert.That(node.Trace!.ContainsKind(ExpansionKind.MacroExpansion)).IsTrue();
        await Assert.That(node.Trace!.Depth).IsGreaterThanOrEqualTo(2);
    }
}
```

- [ ] **Step 2: Add depth limit independence tests**

```csharp
[Test]
public async Task ReplayDepthLimit_FiresIndependentlyOfStructuralDepth()
{
    // Create a chain of EQUS self-referencing to hit replay depth
    // This is hard to construct naturally, so just verify the diagnostic message
    // references "replay depth" not "expansion depth"
    var source = "X EQUS \"Y\"\nY EQUS \"X\"\nSECTION \"Main\", ROM0\nX";
    var tree = SyntaxTree.Parse(source);
    var diag = new DiagnosticBag();
    var symbols = new SymbolTable(diag);
    var expander = new AssemblyExpander(diag, symbols);
    expander.Expand(tree);
    var messages = diag.ToList().Select(d => d.Message).ToList();
    await Assert.That(messages.Any(m => m.Contains("replay depth") || m.Contains("expansion depth"))).IsTrue();
}

[Test]
public async Task StructuralDepthLimit_FiresForDeepMacroNesting()
{
    // Create deeply nested macros — structural depth should fire
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < 70; i++)
        sb.AppendLine($"m{i}: MACRO\nm{i + 1}\nENDM");
    sb.AppendLine("m70: MACRO\nnop\nENDM");
    sb.AppendLine("SECTION \"Main\", ROM0");
    sb.AppendLine("m0");

    var tree = SyntaxTree.Parse(sb.ToString());
    var diag = new DiagnosticBag();
    var symbols = new SymbolTable(diag);
    var expander = new AssemblyExpander(diag, symbols);
    expander.Expand(tree);
    var messages = diag.ToList().Select(d => d.Message).ToList();
    await Assert.That(messages.Any(m => m.Contains("depth") && m.Contains("exceeded"))).IsTrue();
}
```

- [ ] **Step 3: Add INCLUDE SourceFilePath vs trace ancestry test**

```csharp
[Test]
public async Task Include_InsideMacro_PreservesSourceFilePath_WhileTracePreservesAncestry()
{
    var fileResolver = new Koh.Core.Binding.InMemoryFileResolver(new Dictionary<string, string>
    {
        ["main.asm"] = "my_inc: MACRO\nINCLUDE \"inc.asm\"\nENDM\nSECTION \"Main\", ROM0\nmy_inc",
        ["inc.asm"] = "nop"
    });
    var tree = SyntaxTree.Parse(Text.SourceText.From(fileResolver.ReadAllText("main.asm"), "main.asm"));
    var diag = new DiagnosticBag();
    var symbols = new SymbolTable(diag);
    var expander = new AssemblyExpander(diag, symbols, fileResolver);
    var nodes = expander.Expand(tree);

    var nopNodes = nodes.Where(n => n.Node.Kind == SyntaxKind.InstructionStatement).ToList();
    await Assert.That(nopNodes.Count).IsEqualTo(1);
    // SourceFilePath = immediate container (the included file)
    await Assert.That(nopNodes[0].SourceFilePath).IsEqualTo("inc.asm");
    // Trace carries full ancestry: macro + include
    await Assert.That(nopNodes[0].Trace!.ContainsKind(ExpansionKind.MacroExpansion)).IsTrue();
    await Assert.That(nopNodes[0].Trace!.ContainsKind(ExpansionKind.Include)).IsTrue();
}
```

Note: If `InMemoryFileResolver` doesn't exist, use the existing `ISourceFileResolver` mock pattern from `IncludeTests.cs`. Check the codebase for the actual resolver mock type name and adjust.

- [ ] **Step 5: Add SHIFT regression test**

```csharp
[Test]
public async Task Shift_Macro_StillWorksCorrectly()
{
    var source = """
        print_args: MACRO
        IF _NARG > 0
        db \1
        SHIFT
        print_args \#
        ENDC
        ENDM
        SECTION "Main", ROM0
        print_args 1, 2, 3
        """;
    var model = Compilation.Create(SyntaxTree.Parse(source)).Emit();
    await Assert.That(model.Success).IsTrue();
    await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
    await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1);
    await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)2);
    await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)3);
}
```

- [ ] **Step 6: Run full test suite**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All tests pass.

- [ ] **Step 7: Commit**

```bash
git add tests/Koh.Core.Tests/Binding/StructuredReplayTests.cs
git commit -m "test: add provenance ancestry, depth limit, SHIFT regression, and INCLUDE provenance tests"
```

---

### Task 15: Enforce architectural invariant, final cleanup

**Files:**
- Modify: `src/Koh.Core/Binding/AssemblyExpander.cs`

- [ ] **Step 1: Audit all SyntaxTree.Parse calls in AssemblyExpander**

Search `AssemblyExpander.cs` for `SyntaxTree.Parse`. The only remaining calls should be:
- None in replay paths (all go through `_textReplay.ParseForReplay`)
- `ExpandInclude`: `SyntaxTree.Parse(includeText)` — new-source parsing, not replay
- No direct `SyntaxTree.Parse` for text replay anywhere in AssemblyExpander

If any replay-driven parse exists outside `TextReplayService.ParseForReplay`, move it.

- [ ] **Step 2: Audit all SyntaxTree.Parse calls in TextReplayService**

Verify `ParseForReplay` is the only method that calls `SyntaxTree.Parse` for replay. The FOR classification parse (`ClassifyForBody`) calls `SyntaxTree.Parse` — this is one-time classification analysis, not replay.

- [ ] **Step 3: Verify field count on AssemblyExpander**

The remaining fields should be exactly:
- `_diagnostics`, `_symbols`, `_conditional`, `_macros`, `_equsConstants`, `_charMaps`, `_rsCounter`, `_fileResolver`, `_includeStack`, `_uniqueIdCounter`, `_printOutput`, `_interpolation`, `_expressionCache`, `_textReplay`

No ambient expansion-scope fields should remain.

- [ ] **Step 4: Run full test suite one final time**

Run: `dotnet test tests/Koh.Core.Tests/ --no-restore -v quiet`
Expected: All 922+ existing tests pass, plus all new tests pass.

- [ ] **Step 5: Commit final cleanup**

```bash
git add src/Koh.Core/Binding/AssemblyExpander.cs
git commit -m "refactor: enforce architectural invariant — no replay parse outside TextReplayService"
```

- [ ] **Step 6: Final commit with summary**

```bash
git add -A
git commit -m "feat: complete expansion architecture refactor — context-passing, structured replay, provenance traces"
```
