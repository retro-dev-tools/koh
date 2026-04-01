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
        await Assert.That(trace.ContainsKind(ExpansionKind.Include)).IsTrue();
        await Assert.That(trace.FindNearest(ExpansionKind.Include)).IsEqualTo(include);
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
