using Koh.Core.Binding;

namespace Koh.Core.Tests.Binding;

/// <summary>
/// Unit tests for the conditional assembly state machine in isolation.
/// </summary>
public class ConditionalAssemblyStateTests
{
    [Test]
    public async Task Initially_NotSuppressed()
    {
        var state = new ConditionalAssemblyState();
        await Assert.That(state.IsSuppressed).IsFalse();
    }

    [Test]
    public async Task If_True_NotSuppressed()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => true);
        await Assert.That(state.IsSuppressed).IsFalse();
    }

    [Test]
    public async Task If_False_Suppressed()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => false);
        await Assert.That(state.IsSuppressed).IsTrue();
    }

    [Test]
    public async Task If_False_Elif_True_NotSuppressed()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => false);
        state.HandleElif(() => true);
        await Assert.That(state.IsSuppressed).IsFalse();
    }

    [Test]
    public async Task If_True_Elif_True_Suppressed()
    {
        // First branch taken → ELIF skipped even if true
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => true);
        state.HandleElif(() => true);
        await Assert.That(state.IsSuppressed).IsTrue();
    }

    [Test]
    public async Task If_False_Else_NotSuppressed()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => false);
        state.HandleElse();
        await Assert.That(state.IsSuppressed).IsFalse();
    }

    [Test]
    public async Task If_True_Else_Suppressed()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => true);
        state.HandleElse();
        await Assert.That(state.IsSuppressed).IsTrue();
    }

    [Test]
    public async Task Endc_UnsuppressesAfterFalseIf()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => false);
        await Assert.That(state.IsSuppressed).IsTrue();
        state.HandleEndc();
        await Assert.That(state.IsSuppressed).IsFalse();
    }

    [Test]
    public async Task Endc_UnsuppressesAfterTrueIf()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => true);
        state.HandleEndc();
        await Assert.That(state.IsSuppressed).IsFalse();
    }

    [Test]
    public async Task Endc_Orphaned_ReturnsFalse()
    {
        var state = new ConditionalAssemblyState();
        var matched = state.HandleEndc();
        await Assert.That(matched).IsFalse();
    }

    [Test]
    public async Task Endc_Matched_ReturnsTrue()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => true);
        var matched = state.HandleEndc();
        await Assert.That(matched).IsTrue();
    }

    // --- Nesting ---

    [Test]
    public async Task Nested_InnerFalse_OuterTrue_CorrectState()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => true);   // outer true
        state.HandleIf(() => false);  // inner false
        await Assert.That(state.IsSuppressed).IsTrue();
        state.HandleEndc();           // close inner
        await Assert.That(state.IsSuppressed).IsFalse(); // outer still active
        state.HandleEndc();           // close outer
        await Assert.That(state.IsSuppressed).IsFalse();
    }

    [Test]
    public async Task Nested_OuterFalse_InnerNotEvaluated()
    {
        var state = new ConditionalAssemblyState();
        bool innerEvaluated = false;
        state.HandleIf(() => false); // outer false
        state.HandleIf(() => { innerEvaluated = true; return true; }); // inner — should NOT be called
        await Assert.That(innerEvaluated).IsFalse();
        state.HandleEndc(); // close inner
        state.HandleEndc(); // close outer
        await Assert.That(state.IsSuppressed).IsFalse();
    }

    [Test]
    public async Task Nested_InnerElifDoesNotCorruptOuter()
    {
        // The critical nesting bug test from the original review
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => true);   // outer true
        state.HandleIf(() => true);   // inner true
        state.HandleElif(() => false); // inner elif — skipped (branch taken)
        state.HandleEndc();            // close inner
        // Outer must still be "branch taken" — ELSE should be skipped
        state.HandleElse();
        await Assert.That(state.IsSuppressed).IsTrue(); // ELSE must be suppressed
        state.HandleEndc();
        await Assert.That(state.IsSuppressed).IsFalse();
    }

    [Test]
    public async Task Reset_ClearsAllState()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => false);
        await Assert.That(state.IsSuppressed).IsTrue();
        state.Reset();
        await Assert.That(state.IsSuppressed).IsFalse();
        await Assert.That(state.HasUnclosedBlocks).IsFalse();
    }

    [Test]
    public async Task HasUnclosedBlocks_AfterIf()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => true);
        await Assert.That(state.HasUnclosedBlocks).IsTrue();
    }

    [Test]
    public async Task HasUnclosedBlocks_AfterIfEndc()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => true);
        state.HandleEndc();
        await Assert.That(state.HasUnclosedBlocks).IsFalse();
    }

    [Test]
    public async Task Elif_NotEvaluated_WhenDeeplySkipped()
    {
        var state = new ConditionalAssemblyState();
        bool elifEvaluated = false;
        state.HandleIf(() => false);  // depth 1 skip
        state.HandleIf(() => false);  // depth 2 skip (not evaluated — already skipping)
        state.HandleElif(() => { elifEvaluated = true; return true; }); // should NOT be called
        await Assert.That(elifEvaluated).IsFalse();
        state.HandleEndc(); // close inner
        state.HandleEndc(); // close outer
    }

    [Test]
    public async Task Elif_Orphaned_ReturnsFalse()
    {
        var state = new ConditionalAssemblyState();
        var matched = state.HandleElif(() => true);
        await Assert.That(matched).IsFalse();
    }

    [Test]
    public async Task Else_Orphaned_ReturnsFalse()
    {
        var state = new ConditionalAssemblyState();
        var matched = state.HandleElse();
        await Assert.That(matched).IsFalse();
    }

    [Test]
    public async Task Elif_Matched_ReturnsTrue()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => false);
        var matched = state.HandleElif(() => true);
        await Assert.That(matched).IsTrue();
    }

    [Test]
    public async Task Else_Matched_ReturnsTrue()
    {
        var state = new ConditionalAssemblyState();
        state.HandleIf(() => false);
        var matched = state.HandleElse();
        await Assert.That(matched).IsTrue();
    }
}
