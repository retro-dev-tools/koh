using Koh.Core.Binding;
using Koh.Core.Symbols;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

public class EmitModelTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        var binder = new Binder();
        return binder.BindToEmitModel(tree);
    }

    [Test]
    public async Task EmitModel_HasSections()
    {
        var model = Emit("SECTION \"Main\", ROM0\nnop");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections.Count).IsEqualTo(1);
        await Assert.That(model.Sections[0].Name).IsEqualTo("Main");
        await Assert.That(model.Sections[0].Type).IsEqualTo(SectionType.Rom0);
    }

    [Test]
    public async Task EmitModel_SectionData()
    {
        var model = Emit("SECTION \"Main\", ROM0\nnop\nhalt");
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x76);
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2);
    }

    [Test]
    public async Task EmitModel_HasSymbols()
    {
        var model = Emit("MY_CONST EQU $42\nSECTION \"Main\", ROM0\nmain:\nnop");
        await Assert.That(model.Success).IsTrue();

        var constSym = model.Symbols.FirstOrDefault(s => s.Name == "MY_CONST");
        await Assert.That(constSym).IsNotNull();
        await Assert.That(constSym!.Kind).IsEqualTo(SymbolKind.Constant);
        await Assert.That(constSym.Value).IsEqualTo(0x42);

        var labelSym = model.Symbols.FirstOrDefault(s => s.Name == "main");
        await Assert.That(labelSym).IsNotNull();
        await Assert.That(labelSym!.Kind).IsEqualTo(SymbolKind.Label);
    }

    [Test]
    public async Task EmitModel_ExportedLabel()
    {
        var model = Emit("SECTION \"Main\", ROM0\nmain::\nnop");
        var sym = model.Symbols.FirstOrDefault(s => s.Name == "main");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Visibility).IsEqualTo(SymbolVisibility.Exported);
    }

    [Test]
    public async Task EmitModel_ExportDirective()
    {
        var model = Emit("SECTION \"Main\", ROM0\nmain:\nnop\nEXPORT main");
        var sym = model.Symbols.FirstOrDefault(s => s.Name == "main");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Visibility).IsEqualTo(SymbolVisibility.Exported);
    }

    [Test]
    public async Task EmitModel_LocalSymbolNotExported()
    {
        var model = Emit("SECTION \"Main\", ROM0\nmain:\n.loop:\nnop");
        var sym = model.Symbols.FirstOrDefault(s => s.Name == "main.loop");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Visibility).IsEqualTo(SymbolVisibility.Local);
    }

    [Test]
    public async Task EmitModel_ForwardRefResolved()
    {
        // dw target → target is defined after, should be resolved by PatchResolver
        var model = Emit("SECTION \"Main\", ROM0\ndw target\ntarget:\nnop");
        await Assert.That(model.Success).IsTrue();
        // target at offset 2 (after the 2-byte DW)
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x02);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x00);
        // Patch should be resolved — no remaining patches
        await Assert.That(model.Sections[0].Patches.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EmitModel_UndefinedSymbol_Diagnostic()
    {
        var model = Emit("SECTION \"Main\", ROM0\ndw NONEXISTENT");
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d => d.Message.Contains("Undefined"))).IsTrue();
    }

    [Test]
    public async Task EmitModel_MultipleSections()
    {
        var model = Emit(
            "SECTION \"Code\", ROM0\nnop\nSECTION \"Data\", ROM0\ndb $42");
        await Assert.That(model.Sections.Count).IsEqualTo(2);
    }

    [Test]
    public async Task EmitModel_ExportMultipleSymbols()
    {
        var model = Emit(
            "SECTION \"Main\", ROM0\nfoo:\nnop\nbar:\nnop\nEXPORT foo, bar");
        var foo = model.Symbols.FirstOrDefault(s => s.Name == "foo");
        var bar = model.Symbols.FirstOrDefault(s => s.Name == "bar");
        await Assert.That(foo!.Visibility).IsEqualTo(SymbolVisibility.Exported);
        await Assert.That(bar!.Visibility).IsEqualTo(SymbolVisibility.Exported);
    }

    [Test]
    public async Task EmitModel_DiagnosticsEmpty_CleanProgram()
    {
        var model = Emit("MY_CONST EQU $10\nSECTION \"Main\", ROM0\nmain:\nld a, MY_CONST\nhalt");
        await Assert.That(model.Diagnostics).IsEmpty();
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task EmitModel_ForwardRefResolved_Db()
    {
        // db target — forward ref to a label, resolved as Absolute8
        var model = Emit("SECTION \"Main\", ROM0\ndb target\ntarget:\nnop");
        await Assert.That(model.Success).IsTrue();
        // target at offset 1 (after the 1-byte DB placeholder)
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x01);
        await Assert.That(model.Sections[0].Patches.Count).IsEqualTo(0);
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(2); // db + nop
    }

    [Test]
    public async Task EmitModel_MultipleSections_DataIsolated()
    {
        var model = Emit(
            "SECTION \"Alpha\", ROM0\ndb $AA, $BB\nSECTION \"Beta\", ROM0\ndb $CC");
        await Assert.That(model.Sections.Count).IsEqualTo(2);
        var alpha = model.Sections.First(s => s.Name == "Alpha");
        var beta = model.Sections.First(s => s.Name == "Beta");
        await Assert.That(alpha.Data.Length).IsEqualTo(2);
        await Assert.That(alpha.Data[0]).IsEqualTo((byte)0xAA);
        await Assert.That(alpha.Data[1]).IsEqualTo((byte)0xBB);
        await Assert.That(beta.Data.Length).IsEqualTo(1);
        await Assert.That(beta.Data[0]).IsEqualTo((byte)0xCC);
    }

    [Test]
    public async Task EmitModel_ForwardRefResolved_Jr()
    {
        // jr target — forward ref with relative offset resolved by PatchResolver
        var model = Emit("SECTION \"Main\", ROM0\njr target\nnop\ntarget:\nnop");
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x18); // JR opcode
        // target at offset 3 (jr=2 + nop=1), PC after JR = 2, offset = 3 - 2 = 1
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x01);
        await Assert.That(model.Sections[0].Patches.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EmitModel_JrForwardRef_OutOfRange()
    {
        // jr to a target beyond signed byte range
        var nops = string.Concat(Enumerable.Repeat("nop\n", 200));
        var model = Emit($"SECTION \"Main\", ROM0\njr target\n{nops}target:\nnop");
        await Assert.That(model.Success).IsFalse();
        // Either "out of range" or "No valid encoding" — both indicate the error
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("out of range") || d.Message.Contains("No valid encoding"))).IsTrue();
    }

    [Test]
    public async Task EmitModel_ExportMultipleSymbols_NullGuard()
    {
        var model = Emit(
            "SECTION \"Main\", ROM0\nfoo:\nnop\nbar:\nnop\nEXPORT foo, bar");
        var foo = model.Symbols.FirstOrDefault(s => s.Name == "foo");
        var bar = model.Symbols.FirstOrDefault(s => s.Name == "bar");
        await Assert.That(foo).IsNotNull();
        await Assert.That(bar).IsNotNull();
        await Assert.That(foo!.Visibility).IsEqualTo(SymbolVisibility.Exported);
        await Assert.That(bar!.Visibility).IsEqualTo(SymbolVisibility.Exported);
    }
}
