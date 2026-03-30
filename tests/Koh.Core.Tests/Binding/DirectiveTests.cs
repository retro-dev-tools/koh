using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax;
using Koh.Core.Text;

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

    // =========================================================================
    // RGBDS rejection tests
    // =========================================================================

    // RGBDS: align-large-ofs
    [Test]
    public async Task AlignLargeOfs_OffsetEqualsAlignSize_RejectsAssembly()
    {
        // ALIGN[1,2]: offset 2 must be < alignment size 2 — invalid
        var model = Emit("""
            SECTION "Tesst", ROM0, ALIGN[1,2]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: align-offset
    [Test]
    public async Task AlignOffset_OutOfRange_RejectsAssembly()
    {
        // ALIGN[4,18]: offset 18 must be < 16 — invalid
        var model = Emit("""
            SECTION "bad+", ROM0, ALIGN[4, 18]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: section-align-large-ofs
    [Test]
    public async Task SectionAlignLargeOfs_OffsetExceedsSize_RejectsAssembly()
    {
        // ALIGN[2,99]: offset 99 must be < 4 — invalid
        var model = Emit("""
            SECTION "test", ROM0, ALIGN[2, 99]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: align-unattainable
    [Test]
    public async Task AlignUnattainable_UnionWithConflictingAlignment_RejectsAssembly()
    {
        // Second union fragment adds ALIGN[16] which cannot be satisfied in WRAM0
        var model = Emit("""
            SECTION UNION "X", WRAM0
            SECTION UNION "X", WRAM0, ALIGN[16]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: data-in-ram
    [Test]
    public async Task DataInRam_CodeInWram_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "code", WRAM0
            xor a
            SECTION "data", WRAMX
            db 42
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: ds-bad
    [Test]
    public async Task DsBad_UndefinedSizeSymbol_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "test", ROM0[0]
            ds unknown, 0
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: duplicate-section
    [Test]
    public async Task DuplicateSection_SameName_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "sec", ROM0
            SECTION "sec", ROM0
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: fixed-oob
    [Test]
    public async Task FixedOob_AddressOutOfRange_RejectsAssembly()
    {
        // ROM0 range is $0000–$3FFF; $BABE is out of range
        var model = Emit("""
            SECTION "ROM0", ROM0[$BABE]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: fragment-align
    [Test]
    public async Task FragmentAlign_InlineAlignMisaligned_RejectsAssembly()
    {
        // Fragment accumulates 4 bytes then inline ALIGN 2 is unsatisfiable at that offset
        var model = Emit("""
            SECTION FRAGMENT "Frag", ROM0
            db $40
            SECTION FRAGMENT "Frag", ROM0, ALIGN[1]
            db $2e
            SECTION FRAGMENT "Frag", ROM0
            db $1f
            SECTION FRAGMENT "Frag", ROM0
            db $7b
            align 2
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: fragment-align-mismatch
    [Test]
    public async Task FragmentAlignMismatch_IncompatibleFixedAddress_RejectsAssembly()
    {
        var model = Emit("""
            SECTION FRAGMENT "aligned", WRAM0[$c002], ALIGN[1]
            SECTION FRAGMENT "aligned", WRAM0, ALIGN[2]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: fragment-mismatch
    [Test]
    public async Task FragmentMismatch_DifferentFixedAddresses_RejectsAssembly()
    {
        var model = Emit("""
            SECTION FRAGMENT "test", ROM0[0]
            SECTION FRAGMENT "test", ROM0[1]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: impossible-bank
    [Test]
    public async Task ImpossibleBank_HramWithBankClause_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "hram", HRAM, BANK[0]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: incompatible-alignment
    [Test]
    public async Task IncompatibleAlignment_FragmentsTooFarApart_RejectsAssembly()
    {
        // Two 256-byte-aligned ROM0 fragments with 1 byte each cannot be contiguous
        var model = Emit("""
            SECTION FRAGMENT "Test", ROM0, ALIGN[8]
            ds 1
            SECTION FRAGMENT "Test", ROM0, ALIGN[8]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: invalid-alignment
    [Test]
    public async Task InvalidAlignment_ValueExceeds16_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "a", ROMX[$4000], ALIGN[20]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: invalid-bank
    [Test]
    public async Task InvalidBank_VramBankOutOfRange_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "vram", VRAM, BANK[2]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: label-outside-section
    [Test]
    public async Task LabelOutsideSection_GlobalLabelBeforeSection_RejectsAssembly()
    {
        var model = Emit("""
            bad:
            SECTION "Test", ROM0
            good:
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: use-label-outside-section
    [Test]
    public async Task UseLabelOutsideSection_LabelAndRefBeforeSection_RejectsAssembly()
    {
        var model = Emit("""
            lab:
            PRINTLN lab-lab
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: new-pushed-section
    [Test]
    public async Task NewPushedSection_FragmentAlreadyOnStack_RejectsAssembly()
    {
        var model = Emit("""
            SECTION FRAGMENT "A", ROM0
            db 1
            PUSHS
            SECTION FRAGMENT "A", ROM0
            db 2
            POPS
            db 3
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: pushs
    [Test]
    public async Task Pushs_DataOutsideSectionAfterPushs_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "This is invalid", ROM0
            ds 10, 42
            PUSHS
            db 69
            POPS
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: section-union-data
    [Test]
    public async Task SectionUnionData_RomUnion_RejectsAssembly()
    {
        var model = Emit("""
            SECTION UNION "wat", ROM0
            db 42
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: section-union-mismatch
    [Test]
    public async Task SectionUnionMismatch_DifferentFixedAddresses_RejectsAssembly()
    {
        var model = Emit("""
            SECTION UNION "test", WRAM0[$c000]
            SECTION UNION "test", WRAM0[$c001]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: section-name-invalid
    [Test]
    public async Task SectionNameInvalid_ConstantPassedToSectionFunc_RejectsAssembly()
    {
        // SECTION() applied to a non-label constant must fail
        var model = Emit("""
            SECTION "sec", ROM0[0]
            Label:
            DEF Value EQU 42
            println SECTION(Value)
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: charmap-empty
    [Test]
    public async Task CharmapEmpty_EmptyString_RejectsAssembly()
    {
        var model = Emit("""
            CHARMAP "", 1
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: const-and
    [Test]
    public async Task ConstAnd_PcOutsideSection_RejectsAssembly()
    {
        // @ outside a section has no value — should be an error
        var model = Emit("""
            println @ & 0
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: divzero-section-bank
    [Test]
    public async Task DivzeroSectionBank_DivisionByZeroInBankExpr_RejectsAssembly()
    {
        var model = Emit("""
            SECTION "sec", ROMX[1/0]
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // =========================================================================
    // RGBDS: empty-data-directive.asm — DB/DW/DL without data warns in ROM
    // =========================================================================

    [Test]
    public async Task EmptyDataDirective_DbWithoutData_SucceedsWithWarning()
    {
        // RGBDS: empty-data-directive.asm
        // DB, DW, DL with no arguments in ROM sections produce -Wempty-data-directive warnings
        // (not errors). The section still assembles successfully.
        var model = Emit("""
            SECTION "test", ROM0
            ds 1
            db
            dw
            """);
        // Succeeds despite empty directives — warnings only, not errors
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d =>
            d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Warning)).IsTrue();
    }

    [Test]
    public async Task EmptyDataDirective_DlWithoutData_SucceedsWithWarning()
    {
        // RGBDS: empty-data-directive.asm — DL without data in ROM
        var model = Emit("""
            SECTION "test", ROM0
            dl
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics.Any(d =>
            d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Warning)).IsTrue();
    }

    // =========================================================================
    // RGBDS: ds-align.asm — DS ALIGN[n, fill] pads to alignment boundary
    // =========================================================================

    [Test]
    public async Task DsAlign_PadsToAlignmentBoundary()
    {
        // RGBDS: ds-align.asm
        // DS ALIGN[8, $ff] after 5 bytes fills to the next 256-byte boundary
        var model = Emit("""
            SECTION "aligned", ROM0[$0000]
            db 1, 2, 3, 4, 5
            ds align[8, $ff], 6
            db 7
            """);
        await Assert.That(model.Success).IsTrue();
        // ds align[8, $ff], 6 — align to next address where (addr % 256) == 0xff (= 255),
        // using fill value 6. Starting from offset 5: fills offsets 5..254 (250 bytes), then
        // db 7 lands at offset 255. Total section length = 256 bytes.
        var section = model.Sections.First(s => s.Name == "aligned");
        await Assert.That(section.Data.Length).IsEqualTo(256);
        await Assert.That(section.Data[254]).IsEqualTo((byte)6); // fill byte
        await Assert.That(section.Data[255]).IsEqualTo((byte)7); // db 7
    }

    [Test]
    public async Task DsAlign_AlreadyAligned_EmitsZeroBytes()
    {
        // RGBDS: ds-align.asm, ds-align-min.asm — if already aligned, DS ALIGN[n] = 0 bytes
        var model = Emit("""
            SECTION "fixed", ROM0[$100]
            ds align[2]
            db 8, 9, 10
            """);
        await Assert.That(model.Success).IsTrue();
        // $100 is already 4-byte aligned (align[2] = 4 bytes), so ds align[2] = 0 bytes
        var section = model.Sections.First(s => s.Name == "fixed");
        await Assert.That(section.Data.Length).IsEqualTo(3); // just db 8, 9, 10
    }

    [Test]
    public async Task DsAlignMin_UsesMinAlignment()
    {
        // RGBDS: ds-align-min.asm
        // align 3 sets PC to 8-byte boundary. DS ALIGN[4] uses min(3,4)=3 → 8-byte fill
        var model = Emit("""
            SECTION "test", ROM0
            align 3
            db 1, 2, 5
            ds align[4], 0
            db 10, 20
            """);
        await Assert.That(model.Success).IsTrue();
        // After 3 bytes at an 8-byte-aligned offset, DS ALIGN[4] fills 5 bytes → total 8+2=10
        var section = model.Sections.First(s => s.Name == "test");
        await Assert.That(section.Data.Length).IsEqualTo(10);
    }

    [Test]
    public async Task DsAlignOffset_PadsToOffsetWithinAlignment()
    {
        // RGBDS: ds-align-offset.asm
        // DS ALIGN[4, 4] fills to address % 8 == 4
        var model = Emit("""
            SECTION "test", ROM0
            align 3, 3
            db 2, 22, 222
            ds align[4, 4], 0
            db 42
            """);
        await Assert.That(model.Success).IsTrue();
        // 3 bytes at offset 3, then fill 6 bytes to offset 12 (12 % 16 == 12 — no, recalc:
        // align[4,4] = 16-byte boundary + offset 4. After 3 at offset 3: next is 3+6=9?
        // The assertion in rgbds: total == 3+6+1 = 10
        var section = model.Sections.First(s => s.Name == "test");
        await Assert.That(section.Data.Length).IsEqualTo(10);
    }

    // =========================================================================
    // RGBDS: align-increasing.asm — ALIGN with matching pc position
    // =========================================================================

    [Test]
    public async Task AlignIncreasing_AlignWithMatchingOffset_NoPad()
    {
        // RGBDS: align-increasing.asm — ALIGN 4 then ALIGN 4, 2 — already at offset 2
        var model = Emit("""
            SECTION "test1", ROM0
            align 4
            dw $0123
            align 4, 2
            dw $4567
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
    }

    // =========================================================================
    // RGBDS: align-pc.asm — ALIGN with fixed-org section and SRAM
    // =========================================================================

    [Test]
    public async Task AlignPc_FixedOrgSection_AlignWithinSection()
    {
        // RGBDS: align-pc.asm — fixed-address section with ALIGN inside
        var model = Emit("""
            SECTION "align", ROM0, ALIGN[1, 1]
            db 69
            align 1
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // =========================================================================
    // RGBDS: abort-on-missing-incbin*.asm — -MG flag aborts on missing INCBIN
    // =========================================================================

    [Test]
    public async Task AbortOnMissingIncbin_MissingFile_ReportsDiagnostic()
    {
        // RGBDS: abort-on-missing-incbin.asm
        // INCBIN of a nonexistent file should report a "not found" diagnostic
        var vfs = new Koh.Core.VirtualFileResolver();
        var tree = Koh.Core.Syntax.SyntaxTree.Parse(
            Koh.Core.Text.SourceText.From(
                "SECTION \"test\", ROM0\nincbin \"incbin-mg-noexist.bin\"", "main.asm"));
        var binder = new Koh.Core.Binding.Binder(fileResolver: vfs);
        var model = binder.BindToEmitModel(tree);
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("not found"))).IsTrue();
    }

    [Test]
    public async Task AbortOnMissingIncbinSlice_MissingFileWithSlice_ReportsDiagnostic()
    {
        // RGBDS: abort-on-missing-incbin-slice.asm — incbin file, 0, 2
        var vfs = new Koh.Core.VirtualFileResolver();
        var tree = Koh.Core.Syntax.SyntaxTree.Parse(
            Koh.Core.Text.SourceText.From(
                "SECTION \"test\", ROM0\nincbin \"incbin-mg-noexist.bin\", 0, 2", "main.asm"));
        var binder = new Koh.Core.Binding.Binder(fileResolver: vfs);
        var model = binder.BindToEmitModel(tree);
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("not found"))).IsTrue();
    }

    [Test]
    public async Task AbortOnMissingIncbinStart_MissingFileWithStart_ReportsDiagnostic()
    {
        // RGBDS: abort-on-missing-incbin-start.asm — incbin file, 2
        var vfs = new Koh.Core.VirtualFileResolver();
        var tree = Koh.Core.Syntax.SyntaxTree.Parse(
            Koh.Core.Text.SourceText.From(
                "SECTION \"test\", ROM0\nincbin \"incbin-mg-noexist.bin\", 2", "main.asm"));
        var binder = new Koh.Core.Binding.Binder(fileResolver: vfs);
        var model = binder.BindToEmitModel(tree);
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("not found"))).IsTrue();
    }
}
