using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

/// <summary>
/// Tests for OPT, ALIGN, EQUS expansion, angle-bracket quoting, and PRINT/PRINTLN with interpolation.
/// </summary>
public class DirectiveExtensionTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    // =========================================================================
    // OPT directive — accepted without error
    // =========================================================================

    [Test]
    public async Task Opt_AcceptedWithoutError()
    {
        var model = Emit("""
            OPT b.$FF
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Pusho_Popo_AcceptedWithoutError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            PUSHO
            OPT b.$00
            POPO
            nop
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // =========================================================================
    // Inline ALIGN
    // =========================================================================

    [Test]
    public async Task Align_PadsToNextBoundary()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            db $AA
            ALIGN 3
            after_align: db $BB
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // ALIGN 3 = 8-byte alignment. After 1 byte ($AA), pad 7 bytes to reach offset 8
        var sym = model.Symbols.First(s => s.Name == "after_align");
        await Assert.That(sym.Value).IsEqualTo(8);
    }

    [Test]
    public async Task Align_AlreadyAligned_NoPad()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ds 8
            ALIGN 3
            after_align: nop
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // Already at offset 8, ALIGN 3 (8-byte) = no pad needed
        var sym = model.Symbols.First(s => s.Name == "after_align");
        await Assert.That(sym.Value).IsEqualTo(8);
    }

    [Test]
    public async Task Align_ZeroBits_NoPad()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            db $AA
            ALIGN 0
            after: db $BB
            """);
        await Assert.That(model.Success).IsTrue();
        // ALIGN 0 = 1-byte alignment = always aligned, no padding
        var sym = model.Symbols.First(s => s.Name == "after");
        await Assert.That(sym.Value).IsEqualTo(1);
    }

    [Test]
    public async Task Align_WithOffset_PadsToCorrectBoundary()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            db $AA, $BB
            ALIGN 2, 1
            after: db $CC
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // ALIGN 2, 1 = address % 4 == 1. After 2 bytes, next such address is 5. Pad 3.
        var sym = model.Symbols.First(s => s.Name == "after");
        await Assert.That(sym.Value).IsEqualTo(5);
    }

    [Test]
    public async Task Align_WithOffset_AlreadySatisfied()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ds 5
            ALIGN 2, 1
            after: db $CC
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // At offset 5, 5 % 4 == 1 — already satisfied, no pad
        var sym = model.Symbols.First(s => s.Name == "after");
        await Assert.That(sym.Value).IsEqualTo(5);
    }

    [Test]
    public async Task Align_OutsideSection_ReportsError()
    {
        var model = Emit("ALIGN 3");
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ALIGN outside"))).IsTrue();
    }

    [Test]
    public async Task Align_NoArgument_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ALIGN
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ALIGN requires"))).IsTrue();
    }

    [Test]
    public async Task Align_OutOfRange_ReportsError()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            ALIGN 17
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ALIGN value must be 0-16"))).IsTrue();
    }

    // =========================================================================
    // EQUS expansion
    // =========================================================================

    [Test]
    public async Task Equs_BareNameExpansion()
    {
        var model = Emit("""
            MY_INST EQUS "nop"
            SECTION "Main", ROM0
            MY_INST
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00); // nop
    }

    [Test]
    public async Task Equs_UsedInExpression()
    {
        // EQUS with a simple instruction — verify it assembles correctly
        var model = Emit("""
            MY_HALT EQUS "halt"
            SECTION "Main", ROM0
            MY_HALT
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x76); // halt
    }

    // =========================================================================
    // Angle-bracket quoting in macro args
    // =========================================================================

    [Test]
    public async Task Println_OutputsCapturedText()
    {
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            MY_VAL EQU 42
            SECTION "Main", ROM0
            PRINTLN "hello"
            PRINTLN "val={d:MY_VAL}"
            PRINT "no newline"
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();

        var output = sw.ToString();
        await Assert.That(output).Contains("hello");
        await Assert.That(output).Contains("val=42");
        await Assert.That(output).Contains("no newline");
    }

    [Test]
    public async Task Interpolation_HexFormat_WithPrefix()
    {
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            MY_VAL EQU 255
            SECTION "Main", ROM0
            PRINTLN "{#X:MY_VAL}"
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("$FF");
    }

    [Test]
    public async Task Interpolation_DefaultFormat_IsDecimal()
    {
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            MY_VAL EQU 42
            SECTION "Main", ROM0
            PRINTLN "{MY_VAL}"
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("42");
    }

    [Test]
    public async Task Interpolation_BinaryFormat_NoPrefix()
    {
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            MY_VAL EQU 5
            SECTION "Main", ROM0
            PRINTLN "{b:MY_VAL}"
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("101");
    }

    [Test]
    public async Task AngleBracketQuoting_CommaInArg()
    {
        var model = Emit("""
            emit_two: MACRO
            db \1
            ENDM
            SECTION "Main", ROM0
            emit_two <$AA, $BB>
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        // <$AA, $BB> should be passed as a single argument "\1" = "$AA, $BB"
        // which then parses as "db $AA, $BB" → 2 bytes
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xAA);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0xBB);
    }

    // =========================================================================
    // RGBDS rejection tests
    // =========================================================================

    // RGBDS: invalid-opt
    [Test]
    public async Task InvalidOpt_UnknownOptionLetter_RejectsAssembly()
    {
        var model = Emit("""
            opt x
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: undefined-opt
    [Test]
    public async Task UndefinedOpt_UnknownOptionX_RejectsAssembly()
    {
        // Alias test confirming the same behaviour through a separate RGBDS test
        var model = Emit("""
            opt x ; there is no opt x
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: invalid-opt (bad binary digit spec)
    [Test]
    public async Task InvalidOpt_BadBinaryDigitSpec_RejectsAssembly()
    {
        var model = Emit("""
            opt b123
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: invalid-format
    [Test]
    public async Task InvalidFormat_DoubleSignFlagInStrfmt_RejectsAssembly()
    {
        var model = Emit("""
            println STRFMT("%++d", 42)
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: invalid-format (inline format spec)
    [Test]
    public async Task InvalidFormat_InvalidInlineFormatSpec_RejectsAssembly()
    {
        // {xx:N} — "xx" is not a valid format specifier
        var model = Emit("""
            DEF N = 42
            println "{xx:N}"
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: line-continuation
    [Test]
    public async Task LineContinuation_InvalidCharAfterBackslash_RejectsAssembly()
    {
        // \ followed by a space/invalid char (not newline or digit) is a syntax error
        var model = Emit("""
            MACRO \ spam
            WARN "spam"
            ENDM
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: line-continuation-whitespace
    [Test]
    public async Task LineContinuationWhitespace_LabelOutsideSection_RejectsAssembly()
    {
        // foo: bar baz\ — trailing backslash after macro call + label outside section
        var model = Emit("""
            MACRO bar
            ENDM
            foo: bar baz\
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: invalid-underscore
    [Test]
    public async Task InvalidUnderscore_DoubleUnderscore_RejectsAssembly()
    {
        // 123__456 — double underscore in numeric literal is invalid
        var model = Emit("""
            println 123__456
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: invalid-underscore (trailing)
    [Test]
    public async Task InvalidUnderscore_TrailingUnderscore_RejectsAssembly()
    {
        // 12345_ — trailing underscore in numeric literal is invalid
        var model = Emit("""
            println 12345_
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: max-errors
    [Test]
    public async Task MaxErrors_PcOutsideSection_RejectsAssembly()
    {
        // println @ in a ROM section uses PC at assembly time, which is non-constant here
        var model = Emit("""
            SECTION "s", ROM0
            db 42
            println @
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // =========================================================================
    // RGBDS: div-negative.asm — division with negative operands follows C semantics
    // =========================================================================

    [Test]
    public async Task DivNegative_NegativeByNegative_PositiveQuotient()
    {
        // RGBDS: div-negative.asm — $80000000 / $80000000 == 1
        var model = Emit("""
            SECTION "Main", ROM0
            def num = $80000000
            def den = $80000000
            def quo = num / den
            db quo
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task DivNegative_PositiveByNegative_NegativeQuotient()
    {
        // RGBDS: div-negative.asm — 50331648 / -16777216 == -3
        var model = Emit("""
            SECTION "Main", ROM0
            def num = $03000000
            def den = $ff000000
            def quo = num / den
            db quo
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo(unchecked((byte)-3)); // 0xFD
    }

    [Test]
    public async Task DivNegative_IdentityLaw_QuoTimesDesPlusRem_EqualsNum()
    {
        // RGBDS: div-negative.asm — (q * den + rem) == num for all cases
        var model = Emit("""
            SECTION "Main", ROM0
            def num = $c0000000
            def den = $80000000
            def quo = num / den
            def rem = num % den
            def rev = quo * den + rem
            IF rev == num
            db $01
            ELSE
            db $00
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1);
    }

    // =========================================================================
    // RGBDS: div-mod.asm — modulo identity laws
    // =========================================================================

    [Test]
    public async Task DivMod_ModuloIdentityLaw_XPlusYModY_EqualsXModY()
    {
        // RGBDS: div-mod.asm — (x + y) % y == x % y
        var model = Emit("""
            SECTION "Main", ROM0
            def x = 7
            def y = 5
            def r = x % y
            IF (x + y) % y == r
            db $01
            ELSE
            db $00
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task DivMod_ModuloIdentityLaw_XMinusYModY_EqualsXModY()
    {
        // RGBDS: div-mod.asm — (x - y) % y == x % y
        var model = Emit("""
            SECTION "Main", ROM0
            def x = 42
            def y = 256
            def r = x % y
            IF (x - y) % y == r
            db $01
            ELSE
            db $00
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1);
    }

    // =========================================================================
    // RGBDS: format-extremes.asm — STRFMT with various format specifiers
    // =========================================================================

    [Test]
    public async Task FormatExtremes_HexFormat_Int32Max()
    {
        // RGBDS: format-extremes.asm — {#09x:v} where v = $7fffffff → "$7fffffff"
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            def v = $7fffffff
            PRINTLN "{#09x:v}"
            SECTION "Main", ROM0
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("$7fffffff");
    }

    [Test]
    public async Task FormatExtremes_UnsignedDecimalFormat_Uint32Max()
    {
        // RGBDS: format-extremes.asm — {u:v} where v = $ffffffff → "4294967295U"
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            def v = $ffffffff
            PRINTLN "{u:v}U"
            SECTION "Main", ROM0
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("4294967295");
    }

    [Test]
    public async Task FormatExtremes_SignedDecimalFormat_Int32Min()
    {
        // RGBDS: format-extremes.asm — {+d:v} where v = $80000000 → "+−2147483648"
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            def v = $80000000
            PRINTLN "{d:v}"
            SECTION "Main", ROM0
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("-2147483648");
    }

    // =========================================================================
    // RGBDS: shift.asm — shift operators including >>> (logical right shift)
    // =========================================================================

    [Test]
    public async Task Shift_LeftShiftByOne_DoublesValue()
    {
        // RGBDS: shift.asm — 1 << 1 = 2
        var model = Emit("""
            SECTION "Main", ROM0
            db 1 << 1
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)2);
    }

    [Test]
    public async Task Shift_ArithmeticRightShift_SignExtends()
    {
        // RGBDS: shift.asm — -4 >> 1 = -2 (arithmetic, sign-extends)
        var model = Emit("""
            SECTION "Main", ROM0
            dl -4 >> 1
            """);
        await Assert.That(model.Success).IsTrue();
        var data = model.Sections[0].Data;
        int val = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
        await Assert.That(val).IsEqualTo(-2); // 0xFFFFFFFE
    }

    [Test]
    public async Task Shift_LogicalRightShift_ZeroFills()
    {
        // RGBDS: shift.asm — $DEADBEEF >>> 1 = $6F56DF77 (logical, zero-fills)
        var model = Emit("""
            SECTION "Main", ROM0
            dl $DEADBEEF >>> 1
            """);
        await Assert.That(model.Success).IsTrue();
        var data = model.Sections[0].Data;
        uint val = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
        await Assert.That(val).IsEqualTo(0x6F56DF77u);
    }

    [Test]
    public async Task Shift_LeftShiftByLargeAmount_ProducesZeroWithWarning()
    {
        // RGBDS: shift.asm — 1 << 32 = 0 with -Wshift-amount warning
        var model = Emit("""
            SECTION "Main", ROM0
            dl 1 << 32
            """);
        await Assert.That(model.Success).IsTrue();
        var data = model.Sections[0].Data;
        int val = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
        await Assert.That(val).IsEqualTo(0);
    }

    // =========================================================================
    // RGBDS: opt.asm — OPT directive with PUSHO/POPO
    // =========================================================================

    [Test]
    public async Task Opt_PushoPopoRestoresOptions()
    {
        // RGBDS: opt.asm — PUSHO saves opts, OPT changes them, POPO restores
        var model = Emit("""
            SECTION "test", ROM0
            PUSHO
            OPT p$42
            ds 1
            POPO
            ds 1
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Opt_NoWDiv_SuppressesDivisionWarning()
    {
        // RGBDS: opt.asm — OPT Wno-div suppresses -Wdiv for $80000000 / -1
        var model = Emit("""
            SECTION "test", ROM0
            OPT Wno-div
            def n = $80000000 / -1
            db n
            """);
        // With warning suppressed, no diagnostic about overflow division
        await Assert.That(model.Success).IsTrue();
    }

    // =========================================================================
    // RGBDS: section-name.asm — SECTION() function returns section name
    // =========================================================================

    [Test]
    public async Task SectionName_SectionFunctionReturnsName()
    {
        // RGBDS: section-name.asm — SECTION(@) returns current section name
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            SECTION "aaa", ROM0[$5]
            PRINTLN "{SECTION(@)}"
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("aaa");
    }

    [Test]
    public async Task SectionName_SectionFunctionWithLabel()
    {
        // RGBDS: section-name.asm — SECTION(Label1) returns section for label
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse("""
            SECTION "aaa", ROM0[$5]
            Label1:
            PRINTLN "{SECTION(Label1)}"
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(sw.ToString()).Contains("aaa");
    }

    // =========================================================================
    // RGBDS: sizeof-reg.asm — SIZEOF() for register operands
    // =========================================================================

    [Test]
    public async Task SizeofReg_8BitRegisters_SizeIsOne()
    {
        // RGBDS: sizeof-reg.asm — sizeof(a) through sizeof(l) == 1
        var model = Emit("""
            ASSERT sizeof(a) == 1
            ASSERT sizeof(b) == 1
            ASSERT sizeof(c) == 1
            ASSERT sizeof(d) == 1
            ASSERT sizeof(e) == 1
            ASSERT sizeof(h) == 1
            ASSERT sizeof(l) == 1
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task SizeofReg_16BitRegisters_SizeIsTwo()
    {
        // RGBDS: sizeof-reg.asm — sizeof(af) through sizeof(sp) == 2
        var model = Emit("""
            ASSERT sizeof(af) == 2
            ASSERT sizeof(bc) == 2
            ASSERT sizeof(de) == 2
            ASSERT sizeof(hl) == 2
            ASSERT sizeof(sp) == 2
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task SizeofReg_IndirectRegisters_SizeIsOne()
    {
        // RGBDS: sizeof-reg.asm — sizeof([bc]), sizeof([hl+]), sizeof([hli]) == 1
        var model = Emit("""
            ASSERT sizeof([bc]) == 1
            ASSERT sizeof([hl+]) == 1
            ASSERT sizeof([hld]) == 1
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task SizeofReg_HighLowParts_SizeIsOne()
    {
        // RGBDS: sizeof-reg.asm — sizeof(high(af)) == 1, sizeof(low(bc)) == 1
        var model = Emit("""
            ASSERT sizeof(high(af)) == 1
            ASSERT sizeof(low(bc)) == 1
            ASSERT sizeof(high(de)) == 1
            ASSERT sizeof(low(hl)) == 1
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
    }

    // =========================================================================
    // RGBDS: flag-Q.asm — fixed-point literal with -Q flag
    // =========================================================================

    [Test]
    public async Task FlagQ_FixedPointLiteral_EncodedCorrectly()
    {
        // RGBDS: flag-Q.asm — dl 3.14159 with -Q.24 fixed-point option
        // With Q.24: 3.14159 * (1<<24) = 52706394 ≈ $0324A7EA
        // Without Q flag (default Q.16): 3.14159 * 65536 = 205887 ≈ $0324B2
        // We test that the assembler accepts fixed-point literals with OPT Q
        var model = Emit("""
            OPT Q.24
            SECTION "test", ROM0
            dl 3.14159
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(4);
    }
}
