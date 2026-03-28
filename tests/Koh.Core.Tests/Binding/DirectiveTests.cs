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

    // =========================================================================
    // DS — space reservation
    // =========================================================================

    [Test]
    public async Task Ds_LiteralCount_ReservesCorrectBytes()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            DS 4
            nop
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // DS 4 pads with 0x00 by default; nop ($00) follows
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(5);
    }

    [Test]
    public async Task Ds_ForwardDeclaredEquCount_LabelAddressIsCorrect()
    {
        // Regression for: Pass1Data advances PC by 0 when DS count references a forward-declared
        // EQU constant, causing labels after the DS to receive wrong addresses. The pre-scan
        // in AssemblyExpander now defines all EQU constants before Pass 1 runs.
        var model = Emit("""
            SECTION "Main", ROM0[$0000]
            DS PAD_SIZE
            after_pad:
            nop
            PAD_SIZE EQU 4
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();

        var afterPad = model.Symbols.FirstOrDefault(s => s.Name == "after_pad");
        await Assert.That(afterPad).IsNotNull();
        // DS 4 reserves 4 bytes starting at $0000, so after_pad must be at $0004
        await Assert.That(afterPad!.Value).IsEqualTo(4L);
    }

    [Test]
    public async Task Ds_EquCountDefinedBefore_LabelAddressIsCorrect()
    {
        // Confirm the normal (non-forward-reference) case still works correctly.
        var model = Emit("""
            PAD_SIZE EQU 4
            SECTION "Main", ROM0[$0000]
            DS PAD_SIZE
            after_pad:
            nop
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();

        var afterPad = model.Symbols.FirstOrDefault(s => s.Name == "after_pad");
        await Assert.That(afterPad).IsNotNull();
        await Assert.That(afterPad!.Value).IsEqualTo(4L);
    }

    [Test]
    public async Task Ds_EquChainForwardDeclared_LabelAddressIsCorrect()
    {
        // Two-deep constant chain: DS TOTAL_SIZE where TOTAL_SIZE EQU BASE * 2 and BASE EQU 4.
        // Both constants are forward-declared relative to the DS. The pre-scan runs two passes
        // so it can resolve BASE on pass 1, then TOTAL_SIZE on pass 2.
        var model = Emit("""
            SECTION "Main", ROM0[$0000]
            DS TOTAL_SIZE
            after_pad:
            nop
            BASE EQU 4
            TOTAL_SIZE EQU BASE * 2
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();

        var afterPad = model.Symbols.FirstOrDefault(s => s.Name == "after_pad");
        await Assert.That(afterPad).IsNotNull();
        await Assert.That(afterPad!.Value).IsEqualTo(8L); // DS 8 → after_pad at $0008
    }

    [Test]
    public async Task Ds_ThreeDeepEquChain_LabelAddressIsCorrect()
    {
        // Three-deep chain: DS C where C EQU B + 1, B EQU A, A EQU 4.
        // Requires 3 pre-scan passes (the old fixed-2-pass would fail).
        var model = Emit("""
            SECTION "Main", ROM0[$0000]
            DS C_VAL
            after_pad:
            nop
            C_VAL EQU B_VAL + 1
            B_VAL EQU A_VAL
            A_VAL EQU 4
            """);
        await Assert.That(model.Success).IsTrue();

        var afterPad = model.Symbols.FirstOrDefault(s => s.Name == "after_pad");
        await Assert.That(afterPad).IsNotNull();
        await Assert.That(afterPad!.Value).IsEqualTo(5L); // DS 5 → after_pad at $0005
    }
}
