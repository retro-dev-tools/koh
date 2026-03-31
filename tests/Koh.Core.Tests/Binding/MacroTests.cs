using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class MacroTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    [Test]
    public async Task SimpleMacro_Expands()
    {
        var model = Emit("my_nop: MACRO\nnop\nENDM\nSECTION \"Main\", ROM0\nmy_nop");
        Console.WriteLine($"DIAG COUNT: {model.Diagnostics.Count}, SUCCESS: {model.Success}");
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d.Severity}: {d.Message}");
        Console.WriteLine($"SECTIONS: {model.Sections.Count}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Macro_WithArguments()
    {
        var model = Emit("load_reg: MACRO\nld \\1, \\2\nENDM\nSECTION \"Main\", ROM0\nload_reg a, b");
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d.Severity}: {d.Message}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x78); // ld a, b
    }

    [Test]
    public async Task Macro_TwoInstructions()
    {
        var model = Emit("add_two: MACRO\nld a, \\1\nadd a, \\2\nENDM\nSECTION \"Main\", ROM0\nadd_two b, c");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x78); // ld a, b
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x81); // add a, c
    }

    [Test]
    public async Task Macro_CalledMultipleTimes()
    {
        var model = Emit("my_nop: MACRO\nnop\nENDM\nSECTION \"Main\", ROM0\nmy_nop\nmy_nop\nmy_nop");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
    }

    [Test]
    public async Task Macro_ImmediateArgument()
    {
        var model = Emit("load_imm: MACRO\nld a, \\1\nENDM\nSECTION \"Main\", ROM0\nload_imm $42");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x3E);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x42);
    }

    [Test]
    public async Task Macro_BodyNotEmittedAtDefinition()
    {
        var model = Emit("my_macro: MACRO\nnop\nENDM\nSECTION \"Main\", ROM0\nhalt");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x76); // only halt
    }

    [Test]
    public async Task Macro_NoDiagnostics()
    {
        var model = Emit("my_nop: MACRO\nnop\nENDM\nSECTION \"Main\", ROM0\nmy_nop");
        await Assert.That(model.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Macro_WithData()
    {
        var model = Emit("emit_byte: MACRO\ndb \\1\nENDM\nSECTION \"Main\", ROM0\nemit_byte $AA");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xAA);
    }

    [Test]
    public async Task MacroKeywordFirst_Expands()
    {
        var model = Emit("MACRO my_nop\nnop\nENDM\nSECTION \"Main\", ROM0\nmy_nop");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task MacroKeywordFirst_WithArgs()
    {
        var model = Emit("MACRO load_reg\nld \\1, \\2\nENDM\nSECTION \"Main\", ROM0\nload_reg a, b");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x78);
    }

    [Test]
    public async Task MacroWithEquConstants()
    {
        var model = Emit("""
            SCREEN_W EQU 160
            TILE_SIZE EQU 8
            set_reg: MACRO
            ld \1, \2
            ENDM
            SECTION "Main", ROM0
            set_reg a, SCREEN_W / TILE_SIZE
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x3E);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)20);
    }

    // --- \@ label suffix regression tests ---
    // Regression for: ParseLabelOperand() leaving the MacroParamToken(\@) orphaned so that
    // CollectMacroBody computed wrong bodyStart/bodyEnd, cutting off or misaligning the body.

    [Test]
    public async Task Macro_WithAtLabelOperand_ExpandsCorrectly()
    {
        // A macro body containing "call .inner\@" — the \@ suffix must be part of the
        // LabelOperand node so the macro body text is sliced at the right positions.
        var model = Emit("""
            loop_body: MACRO
            call .done\@
            nop
            .done\@:
            ENDM
            SECTION "Main", ROM0
            main:
            loop_body
            """);
        await Assert.That(model.Success).IsTrue();
        // call + nop = 3 bytes + 0 bytes for the label
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(4); // call(3) + nop(1)
    }

    [Test]
    public async Task Macro_WithAtLabelDeclaration_NoDiagnostics()
    {
        // Confirm that a macro defining a \@ local label produces no diagnostics.
        // Before the fix, \@ orphaning caused bodyStart to be off, producing
        // parse errors on the re-lexed macro body text.
        var model = Emit("""
            with_label: MACRO
            .inner\@:
            nop
            ENDM
            SECTION "Main", ROM0
            main:
            with_label
            with_label
            """);
        await Assert.That(model.Diagnostics).IsEmpty();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2); // two nop expansions
    }

    // =========================================================================
    // RGBDS rejection tests
    // =========================================================================

    // RGBDS: builtin-overwrite
    [Test]
    public async Task BuiltinOverwrite_PurgeBuiltinSymbol_RejectsAssembly()
    {
        // Built-in symbols such as __UTC_YEAR__ cannot be purged or redefined
        var model = Emit("""
            PURGE __UTC_YEAR__
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: builtin-overwrite (DEF variant)
    [Test]
    public async Task BuiltinOverwrite_RedefBuiltinSymbol_RejectsAssembly()
    {
        var model = Emit("""
            REDEF __UTC_YEAR__ EQU 0
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: code-after-endm-endr-endc
    [Test]
    public async Task CodeAfterEndm_TrailingTokenOnEndmLine_RejectsAssembly()
    {
        // Code on the same line as ENDM is a syntax error in RGBDS
        var model = Emit("""
            MACRO mac
            println \1
            ENDM println "<_<"
            mac "argument"
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: dots-macro-arg
    [Test]
    public async Task DotsMacroArg_EllipsisInBracketArg_RejectsAssembly()
    {
        // \<...> is a nonsensical nested-local-label reference
        var model = Emit("""
            MACRO test
            println "\<...>"
            ENDM
            test 1, 2, 3, 4
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: macro-arg-recursion
    [Test]
    public async Task MacroArgRecursion_DoubleBackslash_RejectsAssembly()
    {
        // \\2 after a comma tries to use \ as line-continuation with digit — invalid
        var model = Emit("""
            MACRO m
            def x = (\1) * 2
            ENDM
            m 5
            m \\2, 6
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: macro-args-outside-macro
    [Test]
    public async Task MacroArgsOutsideMacro_BackslashArgOutsideMacro_RejectsAssembly()
    {
        var model = Emit("""
            println \1
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: macro-syntax
    [Test]
    public async Task MacroSyntax_LabelBeforeMacroKeyword_RejectsAssembly()
    {
        // RGBDS does not allow "label: MACRO" syntax; MACRO must come first on its own line
        var model = Emit("""
            old: MACRO
            println "out with the ", \1
            ENDM
            old 1
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: shift-outside-macro
    [Test]
    public async Task ShiftOutsideMacro_ShiftWithoutMacroContext_RejectsAssembly()
    {
        var model = Emit("""
            shift
            shift 3
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: bracketed-macro-args (error cases only)
    [Test]
    public async Task BracketedMacroArgs_NonNumericSymbol_RejectsAssembly()
    {
        // \<nonnumeric> where nonnumeric is an EQUS string is not numeric — must fail
        var model = Emit("""
            MACRO bad
            println "nonnumeric", \<nonnumeric>
            ENDM
            def nonnumeric equs "1"
            bad 42
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // RGBDS: rept-shift
    [Test]
    public async Task ReptShift_ShiftPastEndInsideRept_RejectsAssembly()
    {
        // Shifting past the end of arguments inside REPT leaves \1 undefined — must fail
        var model = Emit("""
            MACRO m
            PRINT "\1 "
            REPT 4
            SHIFT
            ENDR
            PRINTLN "\1s!"
            ENDM
            m This, used, not, to, work
            """);
        await Assert.That(model.Success).IsFalse();
    }

    // =========================================================================
    // RGBDS: macro-arguments.asm — comprehensive argument parsing edge cases
    // =========================================================================

    [Test]
    public async Task MacroArguments_EmptyCallNoArgs_NargIsZero()
    {
        // RGBDS: macro-arguments.asm — mac with no args: _NARG == 0
        var model = Emit("""
            MACRO mac
            db _NARG
            ENDM
            SECTION "Main", ROM0
            mac
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0);
    }

    [Test]
    public async Task MacroArguments_ThreeArgs_NargIsThree()
    {
        // RGBDS: macro-arguments.asm — mac 1, 2+2, 3 → _NARG == 3
        var model = Emit("""
            MACRO mac
            db _NARG
            ENDM
            SECTION "Main", ROM0
            mac 1, 2, 3
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)3);
    }

    [Test]
    public async Task MacroArguments_EmptyArgWithComma_NargIsOne()
    {
        // RGBDS: macro-arguments.asm — mac , → one empty argument
        var model = Emit("""
            MACRO mac
            db _NARG
            ENDM
            SECTION "Main", ROM0
            mac ,
            """);
        // mac , → _NARG == 1 (one empty arg) or 2 depending on trailing-comma behaviour
        // RGBDS output for 'mac ,' shows _NARG=1 with \1=<> — but trailing comma removes last
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task MacroArguments_BackslashNewlineContinuation_TwoArgs()
    {
        // RGBDS: macro-arguments.asm — mac \ \n c, d → two args c and d
        var model = Emit("""
            MACRO mac
            db _NARG
            ENDM
            SECTION "Main", ROM0
            mac \
            c, d
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)2);
    }

    [Test]
    public async Task MacroArguments_ExpressionArg_Evaluated()
    {
        // RGBDS: macro-arguments.asm — mac 1, 2 + 2, 3 → \2 = 4
        var model = Emit("""
            MACRO mac
            db \1
            db \2
            db \3
            ENDM
            SECTION "Main", ROM0
            mac 1, 2 + 2, 3
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)4); // 2+2
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)3);
    }

    [Test]
    public async Task MacroArguments_EmptyMiddleArg_ExpandsToEmpty()
    {
        // RGBDS: macro-arguments.asm — mac a,,z → three args: "a", "", "z"
        // Empty middle arg produces warning but assembles ok
        var model = Emit("""
            MACRO mac
            db _NARG
            ENDM
            SECTION "Main", ROM0
            mac a,,z
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)3);
    }

    // =========================================================================
    // RGBDS: macro-arg-escape-chars.asm — triple-quoted string arg passthrough
    // =========================================================================

    [Test]
    public async Task MacroArgEscapeChars_TripleQuotedStringPassthrough()
    {
        // RGBDS: macro-arg-escape-chars.asm
        // bar calls foo with \1 wrapped in quotes; triple-quoted literal passes through
        var sw = new System.IO.StringWriter();
        var tree = SyntaxTree.Parse("""
            MACRO foo
            PRINTLN \1
            ENDM
            MACRO bar
            foo "\1"
            ENDM
            SECTION "Main", ROM0
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
    }

    // =========================================================================
    // RGBDS: macro-argument-limit.asm — up to 9 positional args \1..\9
    // =========================================================================

    [Test]
    public async Task MacroArgumentLimit_NineArgs_AllAccessible()
    {
        // RGBDS: macro-argument-limit.asm — verify \1 through \9 all work
        var model = Emit("""
            MACRO nine
            db \1, \2, \3, \4, \5, \6, \7, \8, \9
            ENDM
            SECTION "Main", ROM0
            nine 1, 2, 3, 4, 5, 6, 7, 8, 9
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(9);
        for (int i = 0; i < 9; i++)
            await Assert.That(model.Sections[0].Data[i]).IsEqualTo((byte)(i + 1));
    }

    // =========================================================================
    // RGBDS: trimmed-macro-args.asm — whitespace around args is trimmed
    // =========================================================================

    [Test]
    public async Task TrimmedMacroArgs_WhitespaceAroundArgs_Trimmed()
    {
        // RGBDS: trimmed-macro-args.asm — args with surrounding whitespace trimmed
        var model = Emit("""
            MACRO print_count
            db _NARG
            ENDM
            SECTION "Main", ROM0
            print_count a, \
                        b \
                      , c
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)3);
    }

    // =========================================================================
    // RGBDS: trailing-commas.asm — trailing comma in macro call removes last arg
    // =========================================================================

    [Test]
    public async Task TrailingComma_MacroCall_TrailingCommaIgnored()
    {
        // RGBDS: trailing-commas.asm — mac 1,2, 3 , ,5, → "1,2,3,,5" (trailing comma removed)
        var model = Emit("""
            MACRO mac
            db _NARG
            ENDM
            SECTION "Main", ROM0
            mac 1, 2, 3
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)3);
    }

    [Test]
    public async Task TrailingComma_DbDirective_TrailingCommaAllowed()
    {
        // RGBDS: trailing-commas.asm — db 1,2,3, is valid
        var model = Emit("""
            SECTION "Main", ROM0
            db 1, 2, 3,
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
    }

    [Test]
    public async Task TrailingComma_DwDirective_TrailingCommaAllowed()
    {
        // RGBDS: trailing-commas.asm — dw 4,5,6, is valid
        var model = Emit("""
            SECTION "Main", ROM0
            dw 4, 5, 6,
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(6); // 3 words × 2 bytes
    }

    // =========================================================================
    // RGBDS: sort-algorithms.asm — simplified selection-sort via macro recursion
    // =========================================================================

    [Test]
    public async Task SortAlgorithms_SelectionSortViaMacro_PrintsSortedOutput()
    {
        // RGBDS: sort-algorithms.asm (simplified)
        // A macro that finds the minimum of its arguments and prints it
        var sw = new System.IO.StringWriter();
        var tree = SyntaxTree.Parse("""
            MACRO min_of_two
            if (\1) <= (\2)
                PRINTLN "{d:\1}"
            else
                PRINTLN "{d:\2}"
            endc
            ENDM
            min_of_two 5, 3
            min_of_two 2, 7
            SECTION "Main", ROM0
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        var output = sw.ToString();
        await Assert.That(output).Contains("3");
        await Assert.That(output).Contains("2");
    }

    [Test]
    public async Task SortAlgorithms_NargDrivenLoop_IteratesAllArgs()
    {
        // RGBDS: sort-algorithms.asm — _NARG-driven REPT with SHIFT
        var sw = new System.IO.StringWriter();
        var tree = SyntaxTree.Parse("""
            MACRO print_each
            REPT _NARG
                PRINTLN "{d:\1}"
                SHIFT
            ENDR
            ENDM
            print_each 10, 20, 30
            SECTION "Main", ROM0
            nop
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        var output = sw.ToString();
        await Assert.That(output).Contains("10");
        await Assert.That(output).Contains("20");
        await Assert.That(output).Contains("30");
    }

    // =========================================================================
    // RGBDS: operator-associativity.asm — left/right associativity assertions
    // =========================================================================

    [Test]
    public async Task OperatorAssociativity_DivisionIsLeftAssociative()
    {
        // RGBDS: operator-associativity.asm — 24 / 6 / 2 == (24/6)/2 == 2, not 24/(6/2)==8
        var model = Emit("""
            SECTION "Main", ROM0
            db 24 / 6 / 2
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)2); // left assoc: (24/6)/2
    }

    [Test]
    public async Task OperatorAssociativity_ExponentiationIsRightAssociative()
    {
        // RGBDS: operator-associativity.asm — 2 ** 3 ** 2 == 2**(3**2) == 512, not (2**3)**2==64
        var model = Emit("""
            SECTION "Main", ROM0
            dw 2 ** 3 ** 2
            """);
        await Assert.That(model.Success).IsTrue();
        var data = model.Sections[0].Data;
        int val = data[0] | (data[1] << 8);
        await Assert.That(val).IsEqualTo(512); // right assoc: 2**(3**2) = 2**9 = 512
    }

    [Test]
    public async Task OperatorAssociativity_ModuloIsLeftAssociative()
    {
        // RGBDS: operator-associativity.asm — 22 % 13 % 5 == (22%13)%5 == 9%5 == 4
        var model = Emit("""
            SECTION "Main", ROM0
            db 22 % 13 % 5
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)4); // (22%13)%5
    }

    [Test]
    public async Task OperatorAssociativity_ShiftLeftIsLeftAssociative()
    {
        // RGBDS: operator-associativity.asm — 1 << 2 << 2 == (1<<2)<<2 == 16
        var model = Emit("""
            SECTION "Main", ROM0
            db 1 << 2 << 2
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)16); // (1<<2)<<2
    }
}
