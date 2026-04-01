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
        var child = ctx.ForLoop(loopFrame, uniqueId: 42);
        await Assert.That(child.LoopDepth).IsEqualTo(1);
        await Assert.That(child.LoopUniqueId).IsEqualTo(42);
        await Assert.That(child.Trace.Current!.Kind).IsEqualTo(ExpansionKind.ReptIteration);
        await Assert.That(ctx.LoopDepth).IsEqualTo(0);
    }

    [Test]
    public async Task ForInclude_SetsFilePathAndIncrementsStructuralDepth()
    {
        var ctx = new ExpansionContext { FilePath = "main.asm" };
        var source = Koh.Core.Text.SourceText.From("nop", "included.asm");
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
        var source = Koh.Core.Text.SourceText.From("nop");
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
