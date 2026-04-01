using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests.Binding;

/// <summary>
/// Tests for structured (non-text-replay) expansion paths and provenance/trace tracking.
/// Verifies that REPT without \@ uses structural replay, FOR with simple variable references
/// uses structural replay, and that depth limits produce the correct diagnostic wording.
/// </summary>
public class StructuredReplayTests
{
    private static List<ExpandedNode> Expand(string source)
    {
        var tree = SyntaxTree.Parse(source);
        var diag = new DiagnosticBag();
        var symbols = new SymbolTable(diag);
        var expander = new AssemblyExpander(diag, symbols);
        return expander.Expand(tree);
    }

    private static List<ExpandedNode> Expand(string source, VirtualFileResolver vfs)
    {
        var text = SourceText.From(source, "main.asm");
        var tree = SyntaxTree.Parse(text);
        var diag = new DiagnosticBag();
        var symbols = new SymbolTable(diag);
        var expander = new AssemblyExpander(diag, symbols, vfs);
        return expander.Expand(tree);
    }

    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    // =========================================================================
    // REPT structural vs text replay classification
    // =========================================================================

    [Test]
    public async Task Rept_WithoutUniqueId_UsesStructuralReplay()
    {
        // REPT without \@ → structural replay → no TextReplay frame in trace
        var nodes = Expand("REPT 3\nnop\nENDR");
        // Each of the 3 nop nodes should come from a ReptIteration frame, NOT a TextReplay frame
        var reptNodes = nodes.Where(n => n.Trace != null &&
            n.Trace.ContainsKind(ExpansionKind.ReptIteration)).ToList();
        await Assert.That(reptNodes.Count).IsEqualTo(3);

        // No node should have a TextReplay frame
        var textReplayNodes = nodes.Where(n => n.Trace != null &&
            n.Trace.ContainsKind(ExpansionKind.TextReplay)).ToList();
        await Assert.That(textReplayNodes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Rept_WithUniqueId_UsesTextReplay()
    {
        // REPT with \@ → text replay is required
        var nodes = Expand("REPT 2\ndb \\@\nENDR");
        var textReplayNodes = nodes.Where(n => n.Trace != null &&
            n.Trace.ContainsKind(ExpansionKind.TextReplay)).ToList();
        await Assert.That(textReplayNodes.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Rept_Structural_HasIterationIndex()
    {
        var nodes = Expand("REPT 3\nnop\nENDR");
        // Each nop should be under a ReptIteration frame
        var reptFrames = nodes
            .Where(n => n.Trace?.FindNearest(ExpansionKind.ReptIteration) != null)
            .Select(n => n.Trace!.FindNearest(ExpansionKind.ReptIteration)!.Iteration)
            .ToList();
        await Assert.That(reptFrames.Count).IsEqualTo(3);
        await Assert.That(reptFrames).Contains(0);
        await Assert.That(reptFrames).Contains(1);
        await Assert.That(reptFrames).Contains(2);
    }

    [Test]
    public async Task DirectSourceNode_HasEmptyTrace()
    {
        var nodes = Expand("nop");
        await Assert.That(nodes.Count).IsGreaterThan(0);
        // A bare nop not inside any expansion context has an empty trace
        var nopNode = nodes.FirstOrDefault();
        await Assert.That(nopNode).IsNotNull();
        await Assert.That(nopNode!.Trace == null || nopNode.Trace.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Rept_Break_WorksWithStructuralReplay()
    {
        // BREAK inside REPT without \@ — structural replay must handle break correctly
        var model = Emit("""
            SECTION "Main", ROM0
            REPT 10
            db 1
            BREAK
            db 2
            ENDR
            """);
        await Assert.That(model.Success).IsTrue();
        // BREAK fires after first db 1, so only 1 byte emitted
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1);
    }

    // =========================================================================
    // FOR structural vs text replay classification
    // =========================================================================

    [Test]
    public async Task For_StandaloneVariable_UsesStructuralReplay()
    {
        // FOR I, 0, 3 with db I → variable only used as IdentifierToken → structural path
        var nodes = Expand("FOR I, 0, 3\ndb I\nENDR");
        // Should have ForIteration frames
        var forNodes = nodes.Where(n => n.Trace != null &&
            n.Trace.ContainsKind(ExpansionKind.ForIteration)).ToList();
        await Assert.That(forNodes.Count).IsGreaterThan(0);
        // Should NOT have any TextReplay frames
        var textReplayNodes = nodes.Where(n => n.Trace != null &&
            n.Trace.ContainsKind(ExpansionKind.TextReplay)).ToList();
        await Assert.That(textReplayNodes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task For_WithUniqueId_UsesTextReplay()
    {
        // FOR body containing \@ → text replay required
        var nodes = Expand("FOR I, 0, 2\nlbl_\\@:\nnop\nENDR");
        var textReplayNodes = nodes.Where(n => n.Trace != null &&
            n.Trace.ContainsKind(ExpansionKind.TextReplay)).ToList();
        await Assert.That(textReplayNodes.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task For_Structural_ProducesCorrectValues()
    {
        // Structural FOR replay produces the correct per-iteration values via synthetic REDEF
        var model = Emit("""
            SECTION "Main", ROM0
            FOR I, 0, 4
            db I
            ENDR
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(4);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)1);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)2);
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)3);
    }

    [Test]
    public async Task For_Structural_HasIterationIndex()
    {
        var nodes = Expand("FOR V, 0, 3\nnop\nENDR");
        var forFrames = nodes
            .Where(n => n.Trace?.FindNearest(ExpansionKind.ForIteration) != null)
            .Select(n => n.Trace!.FindNearest(ExpansionKind.ForIteration)!.Iteration)
            .ToList();
        // 3 nop nodes, each in its own ForIteration frame
        await Assert.That(forFrames.Count).IsGreaterThan(0);
        await Assert.That(forFrames).Contains(0);
        await Assert.That(forFrames).Contains(1);
        await Assert.That(forFrames).Contains(2);
    }

    // =========================================================================
    // Ancestry trace
    // =========================================================================

    [Test]
    public async Task NestedMacroInsideRept_HasFullAncestryTrace()
    {
        // Macro call inside REPT without \@ (structural path) — the macro frame
        // should appear in the trace alongside the ReptIteration frame.
        var nodes = Expand("""
            my_nop: MACRO
            nop
            ENDM
            REPT 2
            my_nop
            ENDR
            """);
        var macroNodes = nodes.Where(n => n.Trace != null &&
            n.Trace.ContainsKind(ExpansionKind.MacroExpansion) &&
            n.Trace.ContainsKind(ExpansionKind.ReptIteration)).ToList();
        await Assert.That(macroNodes.Count).IsEqualTo(2); // 2 iterations × 1 nop per macro
    }

    // =========================================================================
    // Depth limit diagnostics
    // =========================================================================

    [Test]
    public async Task DeepMacroNesting_HitsStructuralDepthLimit()
    {
        // Build a deeply nested macro call chain that exceeds MaxStructuralDepth (64).
        // Each macro calls the next, producing 65+ levels of structural nesting.
        var sb = new System.Text.StringBuilder();
        // m1 calls m2, m2 calls m3, ..., m64 calls m65, m65 calls m1 (self-reference via loop)
        // Simpler: use a self-recursive macro
        sb.AppendLine("recurse: MACRO");
        sb.AppendLine("recurse");
        sb.AppendLine("ENDM");
        sb.AppendLine("recurse");

        var tree = SyntaxTree.Parse(sb.ToString());
        var diag = new DiagnosticBag();
        var symbols = new SymbolTable(diag);
        var expander = new AssemblyExpander(diag, symbols);
        expander.Expand(tree);

        var messages = diag.ToList().Select(d => d.Message).ToList();
        await Assert.That(messages.Any(m => m.Contains("structural depth"))).IsTrue();
    }

    [Test]
    public async Task EqusMutualReference_HitsReplayDepthLimit()
    {
        // alpha EQUS "beta", beta EQUS "alpha" — mutual reference causes unbounded EQUS expansion.
        // Uses multi-letter names to ensure the parser produces MacroCall nodes (single-letter names
        // like A/B are register tokens and parse as nothing at statement level).
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("alpha EQUS \"beta\"");
        sb.AppendLine("beta EQUS \"alpha\"");
        sb.AppendLine("alpha");   // bare-name EQUS expansion: alpha → "beta" → expand beta → "alpha" → ...

        var tree = SyntaxTree.Parse(sb.ToString());
        var diag = new DiagnosticBag();
        var symbols = new SymbolTable(diag);
        var expander = new AssemblyExpander(diag, symbols);
        expander.Expand(tree);

        var messages = diag.ToList().Select(d => d.Message).ToList();
        await Assert.That(messages.Any(m => m.Contains("replay depth"))).IsTrue();
    }

    [Test]
    public async Task ParameterlessMacro_HasMacroTraceButNoTextReplayTrace()
    {
        // A macro with no parameters (no \1..\9, \@, etc.) uses the fast structural path.
        // The trace should show MacroExpansion but NO TextReplay frame.
        var nodes = Expand("my_nop: MACRO\nnop\nENDM\nmy_nop");
        var macroNodes = nodes.Where(n => n.Trace != null &&
            n.Trace.ContainsKind(ExpansionKind.MacroExpansion)).ToList();
        await Assert.That(macroNodes.Count).IsGreaterThan(0);

        // No text replay — parameterless macros use pre-parsed tree directly
        var textReplayNodes = nodes.Where(n => n.Trace != null &&
            n.Trace.ContainsKind(ExpansionKind.TextReplay)).ToList();
        await Assert.That(textReplayNodes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task IncludeInsideMacro_PreservesSourceFilePath_AndAncestry()
    {
        // INCLUDE inside a macro: the included file's nodes should have the included file's
        // SourceFilePath while the trace records both the macro and include ancestry.
        var vfs = new VirtualFileResolver();
        vfs.AddTextFile("inner.inc", "nop");

        var source = """
            my_mac: MACRO
            INCLUDE "inner.inc"
            ENDM
            my_mac
            """;

        var nodes = Expand(source, vfs);
        // The nop from inner.inc should show SourceFilePath == "inner.inc"
        var innerNodes = nodes.Where(n => n.SourceFilePath == "inner.inc").ToList();
        await Assert.That(innerNodes.Count).IsGreaterThan(0);

        // The trace should contain both a MacroExpansion and an Include frame
        var innerNode = innerNodes.First();
        await Assert.That(innerNode.Trace).IsNotNull();
        await Assert.That(innerNode.Trace!.ContainsKind(ExpansionKind.MacroExpansion)).IsTrue();
        await Assert.That(innerNode.Trace.ContainsKind(ExpansionKind.Include)).IsTrue();
    }

    // =========================================================================
    // SHIFT macro regression
    // =========================================================================

    [Test]
    public async Task Shift_MacroRegression_ProducesCorrectOutput()
    {
        // SHIFT should advance the macro argument window, making \1 refer to arg 2, etc.
        // This is the core SHIFT regression — it must still work after the TextReplayService refactor.
        var model = Emit("""
            SECTION "Main", ROM0
            emit_args: MACRO
            IF _NARG > 0
            db \1
            SHIFT
            emit_args \#
            ENDC
            ENDM
            emit_args 10, 20, 30
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)10);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)20);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)30);
    }
}
