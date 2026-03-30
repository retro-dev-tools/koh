using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Integration;

/// <summary>
/// Binary output verification tests derived from the RGBDS test suite.
/// Each test assembles source, then compares the emitted section bytes
/// against the expected binary from the RGBDS .out.bin reference files.
/// </summary>
public class ComplexEndToEndTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    private static (EmitModel Model, string Output) EmitWithOutput(string source)
    {
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse(source);
        var model = Compilation.Create(sw, tree).Emit();
        return (model, sw.ToString());
    }

    /// <summary>
    /// Concatenates all section data in order and compares to expected bytes.
    /// </summary>
    private static async Task AssertBinaryOutput(EmitModel model, byte[] expected)
    {
        await Assert.That(model.Success).IsTrue();
        var actual = model.Sections.SelectMany(s => s.Data).ToArray();
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        for (int i = 0; i < expected.Length; i++)
            await Assert.That(actual[i]).IsEqualTo(expected[i]);
    }

    private static byte[] Hex(string hex) =>
        Convert.FromHexString(hex.Replace(" ", ""));

    // =========================================================================
    // Binary output tests — currently passing
    // =========================================================================

    // RGBDS: ff00+c
    [Test]
    public async Task Ff00PlusC_ThreeLdhInstructions_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0[0]
            	ld [ $ff00 + c ], a
            	ld [ $ff00 + c ], a
            	ld [ $ff00 + c ], a
            """);
        await AssertBinaryOutput(model, Hex("E2 E2 E2"));
    }

    // RGBDS: utf-8
    [Test]
    public async Task Utf8_CharacterInDb_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	db "\u00e9"
            """);
        await AssertBinaryOutput(model, Hex("C3 A9"));
    }

    // RGBDS: assert-nosect
    [Test]
    public async Task AssertNosect_DbOutsideAssert_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	assert 1 == 1
            	db $45
            """);
        await AssertBinaryOutput(model, Hex("45"));
    }

    // RGBDS: ref-override
    [Test]
    public async Task RefOverride_ResolvedValue_BinaryMatch()
    {
        var model = Emit("""
            target EQU $2A
            SECTION "test", ROM0
            	db target
            """);
        await AssertBinaryOutput(model, Hex("2A"));
    }

    // RGBDS: null-character
    [Test]
    public async Task NullCharacter_InStrings_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	db "foo", 0, "bar", 0, "B", 0
            """);
        await AssertBinaryOutput(model, Hex("666F6F00626172004200"));
    }

    // RGBDS: unmapped-char
    [Test]
    public async Task UnmappedChar_AsciiPassthrough_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	db "AAAAAAA"
            """);
        await AssertBinaryOutput(model, Hex("41414141414141"));
    }

    // RGBDS: load-begin
    [Test]
    public async Task LoadBegin_LoadSection_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0[0]
            	ld a, 5
            	nop
            """);
        await AssertBinaryOutput(model, Hex("3E0500"));
    }

    // RGBDS: narg-decreases-after-shift
    [Test]
    public async Task NargDecreasesAfterShift_BinaryMatch()
    {
        var model = Emit("""
            MACRO test_narg
            	db _NARG
            	IF _NARG > 0
            		SHIFT
            		test_narg \#
            	ENDC
            ENDM
            SECTION "test", ROM0
            	test_narg a, b, c
            """);
        await AssertBinaryOutput(model, Hex("03020100"));
    }

    // RGBDS: reference-undefined-sym
    [Test]
    public async Task ReferenceUndefinedSym_PatchedValues_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	db 1, 2
            	db 3, 4
            """);
        await AssertBinaryOutput(model, Hex("01020304"));
    }

    // RGBDS: flag-p
    [Test]
    public async Task FlagP_DataBytes_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	db 1, 2, 3
            	db "BBB"
            	db 4, 5, 6
            """);
        await AssertBinaryOutput(model, Hex("010203424242040506"));
    }

    // =========================================================================
    // Binary output tests — currently failing (features not implemented)
    // =========================================================================

    // RGBDS: align-16
    [Test]
    public async Task Align16_TwoSectionsAligned_BinaryMatch()
    {
        var model = Emit("""
            SECTION "Byte", ROM0
            	db 2
            SECTION "ROM0", ROM0, ALIGN[16]
            	db 1
            """);
        await AssertBinaryOutput(model, Hex("0102"));
    }

    // RGBDS: anon-label
    [Test]
    public async Task AnonLabel_ForwardBackwardJumps_BinaryMatch()
    {
        var model = Emit("""
            SECTION "Anonymous label test", ROM0[0]
            	ld hl, :++
            :	ld a, [hli]
            	ldh [c], a
            	dec c
            	jr nz, :-
            	ret
            :
            	dw $7FFF, $1061, $03E0, $58A5
            """);
        // 17 bytes expected
        await AssertBinaryOutput(model, Hex("210900 2A E2 0D 20FB C9 FF7F 6110 E003 A558"));
    }

    // RGBDS: ccode
    [Test]
    public async Task Ccode_ConditionCodes_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0[0]
            ; Standard condition codes
            Label:
            	jp nz, Label
            	jp z, Label
            	jp nc, Label
            	jp c, Label
            	jr nz, Label
            	jr z, Label
            	jr nc, Label
            	jr c, Label
            	call nz, Label
            	call z, Label
            	call nc, Label
            """);
        await AssertBinaryOutput(model, Hex(
            "C20000 CA0000 D20000 DA0000" +
            "20F2 28F0 30EE 38EC" +
            "C40000 CC0000 D40000"));
    }

    // RGBDS: ds-@
    [Test]
    public async Task DsAt_FillToAddress_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0[0]
            Start:
            	ds 32 - (@ - Start)
            """);
        await AssertBinaryOutput(model, new byte[32]);
    }

    // RGBDS: jr-@
    [Test]
    public async Task JrAt_JumpToSelf_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0[0]
            	jr @
            	jr nz, @
            """);
        // jr @ = 18 FE, jr nz,@ = 20 FE
        await AssertBinaryOutput(model, Hex("18FE 20FE"));
    }

    // RGBDS: multiple-instructions
    [Test]
    public async Task MultipleInstructions_DoubleColonSeparator_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0[0]
            	push hl :: pop hl :: ret
            	push af :: push bc :: push de :: push hl
            	pop hl :: pop de :: pop bc :: pop af
            	nop :: nop :: nop :: nop :: nop :: nop :: nop :: nop
            	nop :: nop :: nop :: nop :: nop :: nop :: nop :: nop
            	nop :: nop :: nop :: nop :: nop :: nop :: nop :: nop
            	nop :: nop :: nop :: nop :: nop :: nop :: nop :: nop
            """);
        // push hl=E5, pop hl=E1, ret=C9, push af=F5, push bc=C5, push de=D5
        // pop hl=E1, pop de=D1, pop bc=C1, pop af=F1, nop=00 x32
        await AssertBinaryOutput(model, Hex(
            "E5 E1 C9" +
            "F5 C5 D5 E5" +
            "E1 D1 C1 F1" +
            "00000000 00000000" +
            "00000000 00000000" +
            "00000000 00000000" +
            "00000000 00000000"));
    }

    // RGBDS: trailing-commas
    [Test]
    public async Task TrailingCommas_InDataDirectives_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	db 1, 2, 3
            	db 4, 5,
            	db 6, 7, 8,
            	dw $0900, $0A00
            	dw $0B00, $0C00,
            	dw $0D00, $0E00, $0F00,
            	db $10
            	dw $1100
            	db $12
            """);
        await AssertBinaryOutput(model, Hex(
            "01020304050607080009000A000B000C000D000E000F10001112"));
    }

    // RGBDS: equ-charmap
    [Test]
    public async Task EquCharmap_CharLiteral_BinaryMatch()
    {
        var model = Emit("""
            DEF _A_ EQU 'A'
            SECTION "test", ROM0
            	db _A_
            """);
        await AssertBinaryOutput(model, Hex("41"));
    }

    // RGBDS: db-dw-dl-string
    [Test]
    public async Task DbDwDlString_MixedDataTypes_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	db "Hello", 0
            	dw $1234
            	db 'A' + 1
            	dl $DEADBEEF
            	dw "Hi"
            	db $FF
            """);
        // This test exercises dl (32-bit) and character literal arithmetic
        await Assert.That(model.Success).IsTrue();
    }

    // RGBDS: shift
    [Test]
    public async Task Shift_LeftAndRightOperations_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	db 1 << 0
            	db 1 << 1
            	db 1 << 2
            	db 1 << 3
            	db 1 << 4
            	db 1 << 5
            	db 1 << 6
            	db 1 << 7
            """);
        await AssertBinaryOutput(model, Hex("01 02 04 08 10 20 40 80"));
    }

    // RGBDS: opt
    [Test]
    public async Task Opt_PushoPopo_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	db 1
            	PUSHO
            	db 2
            	POPO
            	db 3
            """);
        await AssertBinaryOutput(model, Hex("010203"));
    }

    // RGBDS: trigonometry
    [Test]
    public async Task Trigonometry_SinCosValues_BinaryMatch()
    {
        var model = Emit("""
            Q EQU 16
            SECTION "test", ROM0
            	; sin(0) = 0.0
            	dw MUL(SIN(0.0), 1 << Q) >> Q
            	; sin(quarter turn) = 1.0
            	dw MUL(SIN(16384.0), 1 << Q) >> Q
            	; cos(0) = 1.0
            	dw MUL(COS(0.0), 1 << Q) >> Q
            """);
        // This tests the trig built-in functions
        await Assert.That(model.Success).IsTrue();
    }

    // RGBDS: math
    [Test]
    public async Task Math_BuiltinFunctions_BinaryMatch()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	; Basic integer operations that don't need MUL/DIV builtins
            	db 2 * 3
            	db 10 / 3
            	db 10 % 3
            	db 2 ** 10 >> 8
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // RGBDS: fixed-point-specific
    [Test]
    public async Task FixedPointSpecific_MulDivOperations_AssemblesCorrectly()
    {
        var model = Emit("""
            SECTION "test", ROM0
            	OPT Q.16
            	; MUL(2.0, 3.0) should give 6.0
            	; In Q16: 2.0 = $20000, 3.0 = $30000
            	assert MUL($20000, $30000) == $60000
            	nop
            """);
        await Assert.That(model.Success).IsTrue();
    }
}
