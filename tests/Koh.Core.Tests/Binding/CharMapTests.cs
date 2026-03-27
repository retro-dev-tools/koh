using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class CharMapTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    [Test]
    public async Task Charmap_SingleMapping()
    {
        var model = Emit("""
            CHARMAP "A", $41
            SECTION "Main", ROM0
            db "A"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x41);
    }

    [Test]
    public async Task Charmap_CustomMapping()
    {
        var model = Emit("""
            CHARMAP "A", $80
            SECTION "Main", ROM0
            db "A"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80);
    }

    [Test]
    public async Task Charmap_UnmappedCharUsesAscii()
    {
        var model = Emit("""
            CHARMAP "A", $80
            SECTION "Main", ROM0
            db "B"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)'B');
    }

    [Test]
    public async Task Charmap_MultipleChars()
    {
        var model = Emit("""
            CHARMAP "A", $80
            CHARMAP "B", $81
            SECTION "Main", ROM0
            db "AB"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x81);
    }

    [Test]
    public async Task Newcharmap_AutoActivatesAndUsesNewMap()
    {
        var model = Emit("""
            CHARMAP "A", $80
            NEWCHARMAP alt
            CHARMAP "A", $90
            SECTION "Main", ROM0
            db "A"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // NEWCHARMAP auto-activates — no SETCHARMAP needed
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x90);
    }

    [Test]
    public async Task Prechmap_Popcharmap_RestoresAcrossMaps()
    {
        var model = Emit("""
            CHARMAP "A", $80
            PRECHMAP
            NEWCHARMAP alt
            CHARMAP "A", $90
            POPCHARMAP
            SECTION "Main", ROM0
            db "A"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // PRECHMAP saves default map, NEWCHARMAP alt activates alt, POPCHARMAP restores default
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80);
    }

    [Test]
    public async Task Charmap_StringLiteralInDb()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            db "Hi"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)'H');
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)'i');
    }

    [Test]
    public async Task Charmap_NoMapping_AsciiPassthrough()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            db "Z"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)'Z');
    }

    [Test]
    public async Task Setcharmap_SwitchesActiveMap()
    {
        var model = Emit("""
            CHARMAP "A", $80
            NEWCHARMAP alt
            CHARMAP "A", $90
            SETCHARMAP ""
            SECTION "Main", ROM0
            db "A"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // SETCHARMAP "" switches back to default map where A → $80
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x80);
    }

    [Test]
    public async Task MultiCharKey_SurvivesPushcPopc()
    {
        var model = Emit("""
            CHARMAP "AB", $FF
            PUSHC
            NEWCHARMAP alt
            CHARMAP "X", $01
            POPC
            SECTION "Main", ROM0
            db "AB"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // After POPC restores default, multi-char "AB" → $FF should work
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Newcharmap_DuplicateName_ReportsError()
    {
        var model = Emit("""
            NEWCHARMAP foo
            NEWCHARMAP foo
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("already exists"))).IsTrue();
    }

    [Test]
    public async Task Setcharmap_UnknownName_ReportsError()
    {
        var model = Emit("""
            SETCHARMAP nonexistent
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("not found"))).IsTrue();
    }

    [Test]
    public async Task Popcharmap_WithoutPrechmap_ReportsError()
    {
        var model = Emit("""
            POPCHARMAP
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("without matching PUSHC/PRECHMAP"))).IsTrue();
    }

    [Test]
    public async Task Charmap_MultiCharKey_LongestMatch()
    {
        var model = Emit("""
            CHARMAP "AB", $FF
            SECTION "Main", ROM0
            db "AB"
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        // Multi-char mapping: "AB" → single byte $FF
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Charmap_EmptyString_ZeroBytes()
    {
        var model = Emit("""
            SECTION "Main", ROM0
            db ""
            db $01
            """);
        foreach (var d in model.Diagnostics) Console.WriteLine($"  {d}");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x01);
    }
}
