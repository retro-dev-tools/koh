using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class RepeatTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    [Test]
    public async Task Rept_ThreeNops()
    {
        var model = Emit("SECTION \"Main\", ROM0\nREPT 3\nnop\nENDR");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x00);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Rept_Zero_EmitsNothing()
    {
        var model = Emit("SECTION \"Main\", ROM0\nREPT 0\nnop\nENDR\nhalt");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1); // only halt
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x76);
    }

    [Test]
    public async Task Rept_WithEquCount()
    {
        var model = Emit("COUNT EQU 4\nSECTION \"Main\", ROM0\nREPT COUNT\nnop\nENDR");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(4);
    }

    [Test]
    public async Task Rept_MultipleInstructions()
    {
        var model = Emit("SECTION \"Main\", ROM0\nREPT 2\nnop\nhalt\nENDR");
        await Assert.That(model.Success).IsTrue();
        // 2 iterations × 2 instructions = 4 bytes
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(4);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00); // nop
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x76); // halt
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0x00); // nop
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)0x76); // halt
    }

    [Test]
    public async Task Rept_Data()
    {
        var model = Emit("SECTION \"Main\", ROM0\nREPT 3\ndb $AA\nENDR");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xAA);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0xAA);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0xAA);
    }

    [Test]
    public async Task Rept_NoDiagnostics()
    {
        var model = Emit("SECTION \"Main\", ROM0\nREPT 2\nnop\nENDR");
        await Assert.That(model.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task For_BasicLoop()
    {
        // FOR I, 0, 4 → I = 0, 1, 2, 3 (stop exclusive)
        var model = Emit("SECTION \"Main\", ROM0\nFOR I, 0, 4\ndb I\nENDR");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(4);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)1);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)2);
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)3);
    }

    [Test]
    public async Task For_StepDown()
    {
        // FOR CNT, 3, 0, -1 → CNT = 3, 2, 1
        var model = Emit("SECTION \"Main\", ROM0\nFOR CNT, 3, 0, -1\ndb CNT\nENDR");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(3);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)3);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)2);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)1);
    }

    [Test]
    public async Task For_VariableDoesNotCorruptLongerIdentifier()
    {
        // Regression: FOR I, 0, 2 must not replace the 'I' inside 'ITEM_VAL'.
        // With naive string replacement "db ITEM_VAL" → "db 0TEM_VAL" which is a lex error.
        // ITEM_VAL is an EQU constant; the loop variable I is substituted at word boundaries only.
        var model = Emit("""
            ITEM_VAL EQU $55
            SECTION "Main", ROM0
            FOR I, 0, 2
                db ITEM_VAL
            ENDR
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x55);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task For_NoDiagnostics()
    {
        var model = Emit("SECTION \"Main\", ROM0\nFOR IDX, 0, 3\ndb IDX\nENDR");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Rept_InsideIf_True()
    {
        var model = Emit("FLAG EQU 1\nSECTION \"Main\", ROM0\nIF FLAG\nREPT 2\nnop\nENDR\nENDC");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Rept_InsideIf_False()
    {
        var model = Emit("FLAG EQU 0\nSECTION \"Main\", ROM0\nIF FLAG\nREPT 2\nnop\nENDR\nENDC\nhalt");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1); // only halt
    }

    [Test]
    public async Task Rept_WithoutEndr_ProducesDiagnostic()
    {
        var model = Emit("SECTION \"Main\", ROM0\nREPT 3\nnop");
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("REPT/FOR without matching ENDR"))).IsTrue();
    }

    [Test]
    public async Task For_WithStep()
    {
        // FOR I, 0, 10, 2 → I = 0, 2, 4, 6, 8
        var model = Emit("SECTION \"Main\", ROM0\nFOR I, 0, 10, 2\ndb I\nENDR");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(5);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)2);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)4);
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)6);
        await Assert.That(model.Sections[0].Data[4]).IsEqualTo((byte)8);
    }

    [Test]
    public async Task For_StepZero_ProducesDiagnostic()
    {
        var model = Emit("SECTION \"Main\", ROM0\nFOR I, 0, 4, 0\nnop\nENDR");
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("FOR step cannot be zero"))).IsTrue();
    }

    /// <summary>
    /// Regression: FOR variable text substitution must not fire inside string literals.
    /// Without SubstituteOutsideStrings, a naive Regex.Replace on "db \"I\"" where the
    /// loop variable is I would corrupt the string to "db \"0\"", changing the label name
    /// in the re-parsed tree. This test uses a label suffix in a comment string (not a
    /// parseable construct) — instead we verify that an EQU whose name contains the loop
    /// variable letter is unaffected.
    /// </summary>
    [Test]
    public async Task For_VariableSubstitutionRespectsWordBoundaries()
    {
        // FOR I, 0, 2 — body uses both bare 'I' (should substitute) and 'ITEM_VAL'
        // (should NOT substitute because \b only fires at word boundaries and 'I' is
        // adjacent to 'T', another word character).
        // This is equivalent to the existing For_VariableDoesNotCorruptLongerIdentifier
        // but tests the specific boundary between the two substitution modes.
        var model = Emit("""
            ITEM_VAL EQU $77
            SECTION "Main", ROM0
            FOR I, 5, 7
                db I
                db ITEM_VAL
            ENDR
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Diagnostics).IsEmpty();
        // Iteration 1: db 5, db $77. Iteration 2: db 6, db $77.
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(4);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)5);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x77);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)6);
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)0x77);
    }

    // NOTE: Rept_UniqueAt_Labels is deferred. The parser does not yet support \@ embedded
    // within a label name (e.g. .loop\@:) — the lexer emits LocalLabelToken + MacroParamToken
    // + ColonToken rather than treating the whole composite as a single label token.
    // Full \@ support in label names requires a lexer-level change to merge the tokens or
    // a parser-level change to accept <LocalLabelToken> <MacroParamToken> <ColonToken> as a
    // LabelDeclaration. Tracked as a known limitation; the substitution infrastructure is in
    // place (ExpandRept correctly substitutes \@ in bodyTextRaw before re-parsing).
}
