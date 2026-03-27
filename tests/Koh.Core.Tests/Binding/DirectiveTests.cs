using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class DirectiveTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    // =========================================================================
    // ASSERT / STATIC_ASSERT
    // =========================================================================

    [Test]
    public async Task Assert_TrueCondition_NoDiagnostic()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ASSERT 1
            nop
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Assert_FalseCondition_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ASSERT 0
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("Assertion failed"))).IsTrue();
    }

    [Test]
    public async Task Assert_FalseWithMessage()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ASSERT 0, "value must be nonzero"
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("value must be nonzero"))).IsTrue();
    }

    [Test]
    public async Task StaticAssert_TrueCondition_NoDiagnostic()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            STATIC_ASSERT 1
            nop
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task StaticAssert_FalseCondition_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            STATIC_ASSERT 0
            """);
        await Assert.That(model.Success).IsFalse();
    }

    [Test]
    public async Task Assert_WithEquConstant()
    {
        var model = Emit("""
            SIZE EQU 4
            SECTION "Main", ROM0
            ASSERT SIZE == 4
            nop
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
    }

    // =========================================================================
    // WARN
    // =========================================================================

    [Test]
    public async Task Warn_EmitsWarning()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            WARN "this is a warning"
            nop
            """);
        // WARN produces a warning, not an error — Success should still be true
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("this is a warning"))).IsTrue();
    }

    // =========================================================================
    // FAIL
    // =========================================================================

    [Test]
    public async Task Fail_EmitsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            FAIL "build stopped"
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("build stopped"))).IsTrue();
    }

    // =========================================================================
    // PUSHS / POPS
    // =========================================================================

    [Test]
    public async Task Pushs_Pops_RestoresSection()
    {
        var model = Emit("""
            SECTION "First", ROM0
            db $01
            PUSHS
            SECTION "Second", ROM0[$10]
            db $02
            POPS
            db $03
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // After POPS, we should be back in "First"
        var first = model.Sections.First(s => s.Name == "First");
        await Assert.That(first.Data.Length).IsEqualTo(2); // $01 and $03
        await Assert.That(first.Data[0]).IsEqualTo((byte)0x01);
        await Assert.That(first.Data[1]).IsEqualTo((byte)0x03);
    }

    [Test]
    public async Task Pops_WithoutPushs_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            POPS
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("POPS without matching PUSHS"))).IsTrue();
    }

    [Test]
    public async Task Pushs_Pops_NestedSections()
    {
        var model = Emit("""
            SECTION "A", ROM0
            db $AA
            PUSHS
            SECTION "B", ROM0[$10]
            db $BB
            PUSHS
            SECTION "C", ROM0[$20]
            db $CC
            POPS
            db $BD
            POPS
            db $AD
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        var a = model.Sections.First(s => s.Name == "A");
        var b = model.Sections.First(s => s.Name == "B");
        await Assert.That(a.Data[0]).IsEqualTo((byte)0xAA);
        await Assert.That(a.Data[1]).IsEqualTo((byte)0xAD);
        await Assert.That(b.Data[0]).IsEqualTo((byte)0xBB);
        await Assert.That(b.Data[1]).IsEqualTo((byte)0xBD);
    }

    // =========================================================================
    // PRINT / PRINTLN (no-op in binding, just verify no crash)
    // =========================================================================

    [Test]
    public async Task StaticAssert_FalseWithMessage()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            STATIC_ASSERT 0, "static check failed"
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("static check failed"))).IsTrue();
    }

    [Test]
    public async Task StaticAssert_UnresolvableExpr_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            STATIC_ASSERT undefined_symbol == 0
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("could not be evaluated at assembly time"))).IsTrue();
    }

    [Test]
    public async Task Assert_WarnSeverity_ProducesWarning()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ASSERT WARN, 0, "soft warning"
            nop
            """);
        // ASSERT WARN produces a warning, not an error
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Warning &&
            d.Message.Contains("soft warning"))).IsTrue();
    }

    [Test]
    public async Task Assert_FatalSeverity_ProducesError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ASSERT FATAL, 0, "fatal error"
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("fatal error"))).IsTrue();
    }

    [Test]
    public async Task Assert_FailSeverity_ProducesError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ASSERT FAIL, 0
            """);
        await Assert.That(model.Success).IsFalse();
    }

    [Test]
    public async Task Assert_NoCondition_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ASSERT
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ASSERT requires a condition"))).IsTrue();
    }

    [Test]
    public async Task Warn_NoMessage_FallbackText()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            WARN
            nop
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d =>
            d.Severity == DiagnosticSeverity.Warning)).IsTrue();
    }

    [Test]
    public async Task Fail_NoMessage_FallbackText()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            FAIL
            """);
        await Assert.That(model.Success).IsFalse();
    }

    [Test]
    public async Task Print_NoCrash()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            PRINT "hello"
            nop
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Println_NoCrash()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            PRINTLN "hello"
            nop
            """);
        await Assert.That(model.Success).IsTrue();
    }
}
