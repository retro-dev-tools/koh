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
            loop_body
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d.Severity}: {d.Message}");
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
            with_label
            with_label
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d.Severity}: {d.Message}");
        await Assert.That(model.Diagnostics).IsEmpty();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2); // two nop expansions
    }
}
