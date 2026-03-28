using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class ConditionalTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    [Test]
    public async Task If_True_IncludesBranch()
    {
        var model = Emit("MY_FLAG EQU 1\nSECTION \"Main\", ROM0\nIF MY_FLAG\nnop\nENDC");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1); // nop included
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task If_False_SkipsBranch()
    {
        var model = Emit("MY_FLAG EQU 0\nSECTION \"Main\", ROM0\nIF MY_FLAG\nnop\nENDC\nhalt");
        await Assert.That(model.Success).IsTrue();
        // nop skipped, only halt
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x76); // halt
    }

    [Test]
    public async Task If_Else()
    {
        var model = Emit(
            "MY_FLAG EQU 0\nSECTION \"Main\", ROM0\nIF MY_FLAG\nnop\nELSE\nhalt\nENDC");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x76); // halt from ELSE
    }

    [Test]
    public async Task If_True_SkipsElse()
    {
        var model = Emit(
            "MY_FLAG EQU 1\nSECTION \"Main\", ROM0\nIF MY_FLAG\nnop\nELSE\nhalt\nENDC");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00); // nop from IF
    }

    [Test]
    public async Task Elif_SecondBranch()
    {
        var model = Emit("""
            MY_FLAG EQU 2
            SECTION "Main", ROM0
            IF MY_FLAG == 1
                nop
            ELIF MY_FLAG == 2
                halt
            ELIF MY_FLAG == 3
                di
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x76); // halt from ELIF
    }

    [Test]
    public async Task Elif_FallsToElse()
    {
        var model = Emit("""
            MY_FLAG EQU 99
            SECTION "Main", ROM0
            IF MY_FLAG == 1
                nop
            ELIF MY_FLAG == 2
                halt
            ELSE
                di
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xF3); // di from ELSE
    }

    [Test]
    public async Task NestedIf()
    {
        var model = Emit("""
            OUTER EQU 1
            INNER EQU 0
            SECTION "Main", ROM0
            IF OUTER
                nop
                IF INNER
                    halt
                ENDC
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        // outer is true → nop emitted; inner is false → halt skipped
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task NestedIf_OuterFalse_InnerSkipped()
    {
        var model = Emit("""
            OUTER EQU 0
            SECTION "Main", ROM0
            IF OUTER
                IF 1
                    nop
                ENDC
            ENDC
            halt
            """);
        await Assert.That(model.Success).IsTrue();
        // outer false → everything inside skipped, only halt
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x76);
    }

    [Test]
    public async Task If_Expression()
    {
        var model = Emit("""
            VAL EQU 5
            SECTION "Main", ROM0
            IF VAL > 3
                nop
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
    }

    [Test]
    public async Task If_DefFunction()
    {
        var model = Emit("""
            MY_SYM EQU 1
            SECTION "Main", ROM0
            IF DEF(MY_SYM)
                nop
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
    }

    [Test]
    public async Task NestedIf_InnerElifDoesNotCorruptOuterBranch()
    {
        // This is the critical nesting test: inner IF with ELIF must NOT corrupt
        // the outer IF's branch-taken state. Without a stack, inner ENDC resets
        // _branchTaken to false, causing the outer ELSE to be incorrectly taken.
        var model = Emit("""
            OUTER EQU 1
            INNER EQU 1
            SECTION "Main", ROM0
            IF OUTER
                nop
                IF INNER
                    halt
                ELIF 0
                ENDC
            ELSE
                di
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        // outer is true → nop + halt emitted; ELSE skipped (di NOT emitted)
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00); // nop
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x76); // halt
    }

    [Test]
    public async Task EndcWithoutIf_ProducesDiagnostic()
    {
        var model = Emit("SECTION \"Main\", ROM0\nENDC");
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ENDC without"))).IsTrue();
    }

    [Test]
    public async Task ElifWithoutIf_ProducesDiagnostic()
    {
        var model = Emit("SECTION \"Main\", ROM0\nELIF 1\nnop\nENDC");
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ELIF without"))).IsTrue();
    }

    [Test]
    public async Task ElseWithoutIf_ProducesDiagnostic()
    {
        var model = Emit("SECTION \"Main\", ROM0\nELSE\nnop\nENDC");
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("ELSE without"))).IsTrue();
    }

    /// <summary>
    /// Regression: EQU constants must be defined exactly once (by the AssemblyExpander).
    /// Pass 1 must NOT attempt to redefine them, which would produce a spurious
    /// "already defined" diagnostic. The model must be fully successful with no diagnostics.
    /// </summary>
    [Test]
    public async Task Equ_BeforeIf_NoDuplicateDefinitionDiagnostic()
    {
        var model = Emit("""
            MY_FLAG EQU 1
            SECTION "Main", ROM0
            IF MY_FLAG
                nop
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00); // nop
    }

    /// <summary>
    /// Regression: Two distinct EQU constants must each produce exactly one definition.
    /// Neither should cause a duplicate-definition diagnostic.
    /// </summary>
    [Test]
    public async Task MultipleEqu_NoDuplicateDefinitionDiagnostics()
    {
        // Note: single-letter names A/B/C/D/E/H/L are register keywords in SM83;
        // use multi-character names for user-defined constants.
        var model = Emit("""
            FLAG_ONE EQU 1
            FLAG_TWO EQU 2
            SECTION "Main", ROM0
            IF FLAG_ONE
                nop
            ENDC
            IF FLAG_TWO
                halt
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00); // nop
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x76); // halt
    }

    [Test]
    public async Task IfWithoutEndc_ProducesDiagnostic()
    {
        var model = Emit("FLAG EQU 1\nSECTION \"Main\", ROM0\nIF FLAG\nnop");
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("missing ENDC"))).IsTrue();
    }

    [Test]
    public async Task If_NoDiagnostics()
    {
        var model = Emit("""
            FLAG EQU 1
            SECTION "Main", ROM0
            IF FLAG
                nop
            ELSE
                halt
            ENDC
            """);
        await Assert.That(model.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task ConditionalAssembly_IfElse()
    {
        var model = Emit("""
            GBC_MODE EQU 1
            SECTION "Main", ROM0
            IF GBC_MODE
            ld a, $80
            ELSE
            ld a, $00
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x80);
    }

    [Test]
    public async Task RgbdsBuiltins_VersionCheck()
    {
        var model = Emit("""
            IF __RGBDS_MAJOR__ >= 1
            SECTION "Main", ROM0
            db $AA
            ENDC
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xAA);
    }
}
