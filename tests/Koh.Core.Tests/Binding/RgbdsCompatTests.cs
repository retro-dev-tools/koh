using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests.Binding;

/// <summary>
/// Tests for RGBDS-specific macro/REPT/FOR behaviors identified by the
/// RGBDS macro consultant expert.
/// </summary>
public class RgbdsCompatTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    // =========================================================================
    // \@ in REPT — unique labels per iteration
    // =========================================================================

    [Test]
    public async Task Rept_UniqueLabelsWithBackslashAt()
    {
        // \@ produces a unique suffix per iteration so labels don't collide
        var model = Emit("SECTION \"Main\", ROM0\nREPT 3\nnop\nENDR");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
    }

    // =========================================================================
    // _NARG as a real symbol
    // =========================================================================

    [Test]
    public async Task Macro_NargAsSymbol()
    {
        // _NARG must be a real symbol, not text substitution
        // This means db _NARG should produce the argument count as a byte
        var model = Emit("count_args: MACRO\ndb _NARG\nENDM\nSECTION \"Main\", ROM0\ncount_args a, b, c");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)3);
    }

    // =========================================================================
    // Paren-depth in macro args
    // =========================================================================

    [Test]
    public async Task Macro_ParenDepthInArgs()
    {
        // Commas inside parentheses don't split: BANK(x), y → 2 args
        var model = Emit("emit_two: MACRO\ndb \\1\ndb \\2\nENDM\nSECTION \"Main\", ROM0\nemit_two HIGH($AABB), LOW($CCDD)");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xAA); // HIGH($AABB)
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0xDD); // LOW($CCDD)
    }

    // =========================================================================
    // FOR variable as real symbol
    // =========================================================================

    [Test]
    public async Task For_VariableIsRealSymbol()
    {
        // After FOR loop, the variable retains its last value
        var model = Emit("SECTION \"Main\", ROM0\nFOR I, 0, 4\nnop\nENDR\ndb I");
        await Assert.That(model.Success).IsTrue();
        // 4 nops + db I (I = last value which doesn't satisfy loop condition, so I = 3 at last iteration)
        // Actually: FOR I, 0, 4 → I = 0,1,2,3. After loop, I = 3 (last iteration value)
        // Then db I → db 3
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(5); // 4 nops + 1 db
        await Assert.That(model.Sections[0].Data[4]).IsEqualTo((byte)3);
    }

    // =========================================================================
    // Recursion depth limit
    // =========================================================================

    [Test]
    public async Task Macro_RecursionLimit()
    {
        // Infinite recursion must produce a clean diagnostic, not a stack overflow
        var model = Emit("recurse: MACRO\nrecurse\nENDM\nSECTION \"Main\", ROM0\nrecurse");
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("Maximum") && d.Message.Contains("depth"))).IsTrue();
    }

    // =========================================================================
    // \# — all remaining args
    // =========================================================================

    [Test]
    public async Task Macro_BackslashHash_AllArgs()
    {
        // \# expands to all arguments as comma-separated string
        // In a simple case: emit_all a, b → \# = "a, b"
        // We test with a macro that uses \# to forward args
        var model = Emit("fwd: MACRO\ndb \\#\nENDM\nSECTION \"Main\", ROM0\nfwd $01, $02, $03");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x01);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x02);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0x03);
    }

    // =========================================================================
    // _NARG save/restore across nested macro calls
    // =========================================================================

    /// <summary>
    /// _NARG must be correctly restored to the outer macro's argument count
    /// after an inner macro call completes. Without the try/finally fix, the
    /// outer macro would see the inner macro's _NARG value after the nested call.
    /// </summary>
    [Test]
    public async Task Macro_NargRestoredAfterNestedCall()
    {
        // outer calls inner; after inner returns, outer's _NARG must still be 2
        var model = Emit("""
            inner: MACRO
                db _NARG
            ENDM
            outer: MACRO
                inner $FF
                db _NARG
            ENDM
            SECTION "Main", ROM0
            outer $01, $02
            """);
        await Assert.That(model.Success).IsTrue();
        // inner is called with 1 arg → db 1; then outer emits db _NARG (outer has 2 args) → db 2
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1); // inner's _NARG
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)2); // outer's _NARG restored
    }

    // =========================================================================
    // RGBDS: multiple-instructions.asm — :: separator on one line
    // =========================================================================

    [Test]
    public async Task MultipleInstructions_ColonColonSeparator_AllEmitted()
    {
        // RGBDS: multiple-instructions.asm — push hl :: pop hl :: ret on one line
        var model = Emit("""
            SECTION "test", ROM0
            push hl :: pop hl :: ret
            """);
        await Assert.That(model.Success).IsTrue();
        // push hl (1) + pop hl (1) + ret (1) = 3 bytes
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xE5); // push hl
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0xE1); // pop hl
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0xC9); // ret
    }

    [Test]
    public async Task MultipleInstructions_MixedDataAndInstructions_AllEmitted()
    {
        // RGBDS: multiple-instructions.asm — Label3:: db 1, 2 :: dw 3, 4 :: ds 7, 8 :: ret
        var model = Emit("""
            SECTION "test", ROM0
            Label3:: db 1, 2 :: dw 3, 4 :: ds 7, 8 :: ret
            """);
        await Assert.That(model.Success).IsTrue();
        // db 1,2 (2) + dw 3,4 (4) + ds 7,8 (7) + ret (1) = 14 bytes
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(14);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)1);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)2);
        await Assert.That(model.Sections[0].Data[13]).IsEqualTo((byte)0xC9); // ret
    }

    [Test]
    public async Task MultipleInstructions_BackslashLineContinuation()
    {
        // RGBDS: multiple-instructions.asm — nop :: ld a, \ b :: ret
        var model = Emit("""
            SECTION "test", ROM0
            nop :: ld a, \
            b :: ret
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00); // nop
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x78); // ld a, b
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0xC9); // ret
    }

    // =========================================================================
    // RGBDS: rst.asm — RST instruction with forward-declared targets
    // =========================================================================

    [Test]
    public async Task Rst_StandardVectors_Encoded()
    {
        // RGBDS: rst.asm — RST $00 through RST $38 forward-reference resolved
        var model = Emit("""
            SECTION "calls", ROM0[$0]
            rst $00
            rst $08
            rst $10
            rst $18
            rst $20
            rst $28
            rst $30
            rst $38
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xC7); // rst $00
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0xCF); // rst $08
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0xD7); // rst $10
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)0xDF); // rst $18
        await Assert.That(model.Sections[0].Data[4]).IsEqualTo((byte)0xE7); // rst $20
        await Assert.That(model.Sections[0].Data[5]).IsEqualTo((byte)0xEF); // rst $28
        await Assert.That(model.Sections[0].Data[6]).IsEqualTo((byte)0xF7); // rst $30
        await Assert.That(model.Sections[0].Data[7]).IsEqualTo((byte)0xFF); // rst $38
    }

    [Test]
    public async Task Rst_LabelSymbol_ForwardReferenceResolved()
    {
        // RGBDS: rst.asm — rst uses label as target; value must be RST-encodable
        var model = Emit("""
            SECTION "calls", ROM0[$0]
            rst target
            SECTION "rst00", ROM0[$00]
            target:
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections.First(s => s.Name == "calls").Data[0]).IsEqualTo((byte)0xC7);
    }

    // =========================================================================
    // RGBDS: ccode.asm — condition codes including ! (NOT) modifier
    // =========================================================================

    [Test]
    public async Task Ccode_NotNz_EncodesAsZ()
    {
        // RGBDS: ccode.asm — jp !nz, Label encodes as jp z (flipped condition)
        var model = Emit("""
            SECTION "ccode test", ROM0[$0]
            Label:
            .local1
            jp z, Label
            jr nz, .local1
            jp !nz, Label
            jr !z, .local1
            """);
        await Assert.That(model.Success).IsTrue();
        // jp z = $CA, jp !nz = jp z = $CA (same encoding)
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xCA); // jp z
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)0x20); // jr nz
        await Assert.That(model.Sections[0].Data[5]).IsEqualTo((byte)0xCA); // jp !nz = jp z
    }

    [Test]
    public async Task Ccode_DoubleNot_SameAsOriginal()
    {
        // RGBDS: ccode.asm — jp !!z, Label encodes same as jp z
        var model = Emit("""
            SECTION "ccode test", ROM0[$0]
            Label:
            jp !!z, Label
            jr !!nz, Label
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xCA); // jp !!z = jp z
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)0x20); // jr !!nz = jr nz
    }

    // =========================================================================
    // RGBDS: destination-a.asm — ALU ops with and without explicit 'a' destination
    // =========================================================================

    [Test]
    public async Task DestinationA_AddWithExplicitA_SameAsWithout()
    {
        // RGBDS: destination-a.asm — add b and add a, b encode identically
        var model = Emit("""
            SECTION "test", ROM0[$0]
            add b
            add a, b
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo(model.Sections[0].Data[1]);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80); // add a, b
    }

    [Test]
    public async Task DestinationA_CplNoOperand_EncodesCorrectly()
    {
        // RGBDS: destination-a.asm — cpl and cpl a both encode as $2F
        var model = Emit("""
            SECTION "test", ROM0[$0]
            cpl
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x2F);
    }

    [Test]
    public async Task DestinationA_XorImmediate_Encoded()
    {
        // RGBDS: destination-a.asm — xor $80 and xor a, $80 encode identically
        var model = Emit("""
            SECTION "test", ROM0[$0]
            xor $80
            xor a, $80
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xEE);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x80);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0xEE);
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)0x80);
    }

    // =========================================================================
    // RGBDS: preinclude.asm — -P pre-include flag behaviour
    // =========================================================================

    [Test]
    public async Task Preinclude_PreIncludedFile_SymbolsAvailable()
    {
        // RGBDS: preinclude.asm — symbols from pre-included files are available
        // We simulate by including the file directly in source
        var vfs = new Koh.Core.VirtualFileResolver();
        vfs.AddTextFile("preinclude-1.inc", "def v1 = 22");
        vfs.AddTextFile("preinclude-2.inc", "def v2 = 24");
        var tree = Koh.Core.Syntax.SyntaxTree.Parse(
            Koh.Core.Text.SourceText.From("""
                INCLUDE "preinclude-1.inc"
                INCLUDE "preinclude-2.inc"
                def v3 = v1 + v2
                SECTION "Main", ROM0
                db v3
                """, "main.asm"));
        var binder = new Koh.Core.Binding.Binder(fileResolver: vfs);
        var model = binder.BindToEmitModel(tree);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)46); // 22 + 24
    }

    // =========================================================================
    // RGBDS: utc-time.asm — __UTC_YEAR__, __UTC_MONTH__ etc. built-ins
    // =========================================================================

    [Test]
    public async Task UtcTime_BuiltinDateSymbols_InValidRange()
    {
        // RGBDS: utc-time.asm — UTC date/time built-ins are in sensible ranges
        var model = Emit("""
            ASSERT __UTC_YEAR__ >= 0 && __UTC_YEAR__ <= 9999
            ASSERT __UTC_MONTH__ >= 1 && __UTC_MONTH__ <= 12
            ASSERT __UTC_DAY__ >= 1 && __UTC_DAY__ <= 31
            ASSERT __UTC_HOUR__ >= 0 && __UTC_HOUR__ <= 23
            ASSERT __UTC_MINUTE__ >= 0 && __UTC_MINUTE__ <= 59
            ASSERT __UTC_SECOND__ >= 0 && __UTC_SECOND__ <= 60
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task UtcTime_Iso8601Builtin_MatchesFormatted()
    {
        // RGBDS: utc-time.asm — __ISO_8601_UTC__ matches STRCAT of date parts
        var model = Emit("""
            DEF UTC_TIME EQUS STRCAT("{04d:__UTC_YEAR__}-{02d:__UTC_MONTH__}-{02d:__UTC_DAY__}T", \
                                 "{02d:__UTC_HOUR__}:{02d:__UTC_MINUTE__}:{02d:__UTC_SECOND__}Z")
            ASSERT !STRCMP("{UTC_TIME}", __ISO_8601_UTC__)
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // =========================================================================
    // RGBDS: state-features.asm — -s flag outputs assembler state
    // =========================================================================

    [Test]
    public async Task StateFeatures_MacroDefinedAndCharmap_Assembles()
    {
        // RGBDS: state-features.asm — macro, charmap, EQUS, variable, EQU definitions
        var model = Emit("""
            MACRO one
            nop
            ENDM
            CHARMAP "char", 2, 3
            DEF string EQUS "four"
            DEF variable = 5
            DEF constant EQU 6
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "constant")).IsTrue();
        await Assert.That(model.Symbols.Any(s => s.Name == "variable")).IsTrue();
    }

    // =========================================================================
    // RGBDS: include-unique-id.asm — \@ unique IDs propagate into INCLUDE'd files
    // =========================================================================

    [Test]
    public async Task IncludeUniqueId_MacroInvokedWithArgs_UniqueIdInIncluded()
    {
        // RGBDS: include-unique-id.asm — \@ in macro body is same inside INCLUDE'd file
        var sw = new System.IO.StringWriter();
        var vfs = new Koh.Core.VirtualFileResolver();
        vfs.AddTextFile("inner.inc", "PRINTLN \"inner: \\@\"");
        var tree = Koh.Core.Syntax.SyntaxTree.Parse(
            Koh.Core.Text.SourceText.From("""
                MACRO mac
                PRINTLN "outer: \@"
                INCLUDE "inner.inc"
                ENDM
                mac hello
                mac world
                SECTION "Main", ROM0
                nop
                """, "main.asm"));
        var binder = new Binder(fileResolver: vfs, printOutput: sw);
        var model = binder.BindToEmitModel(tree);
        await Assert.That(model.Success).IsTrue();
        var lines = sw.ToString().Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
                      .Select(l => l.Trim()).ToList();
        // outer and inner lines for each invocation must share the same \@ ID
        // e.g. "outer: _u1" and "inner: _u1", then "outer: _u2" and "inner: _u2"
        await Assert.That(lines.Count).IsEqualTo(4);
        // outer:_u1 and inner:_u1 share the same suffix
        string outerSuffix1 = lines[0].Split(' ').Last();
        string innerSuffix1 = lines[1].Split(' ').Last();
        await Assert.That(outerSuffix1).IsEqualTo(innerSuffix1);
        // invocations 1 and 2 have different IDs
        string outerSuffix2 = lines[2].Split(' ').Last();
        await Assert.That(outerSuffix1).IsNotEqualTo(outerSuffix2);
    }

    [Test]
    public async Task IncludeUniqueId_ReptWithInclude_UniqueIdShared()
    {
        // RGBDS: include-unique-id.asm — \@ in REPT body same inside INCLUDE
        var sw = new System.IO.StringWriter();
        var vfs = new Koh.Core.VirtualFileResolver();
        vfs.AddTextFile("inner.inc", "PRINTLN \"inner: \\@\"");
        var tree = Koh.Core.Syntax.SyntaxTree.Parse(
            Koh.Core.Text.SourceText.From("""
                REPT 2
                PRINTLN "outer: \@"
                INCLUDE "inner.inc"
                ENDR
                SECTION "Main", ROM0
                nop
                """, "main.asm"));
        var binder = new Binder(fileResolver: vfs, printOutput: sw);
        var model = binder.BindToEmitModel(tree);
        await Assert.That(model.Success).IsTrue();
        var lines = sw.ToString().Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
                      .Select(l => l.Trim()).ToList();
        await Assert.That(lines.Count).IsEqualTo(4);
        string outerSuffix1 = lines[0].Split(' ').Last();
        string innerSuffix1 = lines[1].Split(' ').Last();
        await Assert.That(outerSuffix1).IsEqualTo(innerSuffix1);
    }
}
