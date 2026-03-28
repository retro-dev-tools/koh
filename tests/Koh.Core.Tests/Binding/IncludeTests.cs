using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests.Binding;

public class IncludeTests
{
    private static EmitModel Emit(string source, VirtualFileResolver? vfs = null)
    {
        var tree = SyntaxTree.Parse(SourceText.From(source, "main.asm"));
        var binder = new Binder(fileResolver: vfs);
        return binder.BindToEmitModel(tree);
    }

    // =========================================================================
    // INCLUDE
    // =========================================================================

    [Test]
    public async Task Include_BasicFile()
    {
        var vfs = new VirtualFileResolver();
        vfs.AddTextFile("defs.inc", "MY_CONST EQU $42");

        var model = Emit(
            "INCLUDE \"defs.inc\"\nSECTION \"Main\", ROM0\ndb MY_CONST",
            vfs);

        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x42);
    }

    [Test]
    public async Task Include_MacroFromIncludedFile()
    {
        var vfs = new VirtualFileResolver();
        vfs.AddTextFile("macros.inc", "my_nop: MACRO\nnop\nENDM");

        var model = Emit(
            "INCLUDE \"macros.inc\"\nSECTION \"Main\", ROM0\nmy_nop",
            vfs);

        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Include_LabelFromIncludedFile()
    {
        var vfs = new VirtualFileResolver();
        vfs.AddTextFile("code.inc", "helper:\nnop\nret");

        var model = Emit(
            "SECTION \"Main\", ROM0\nINCLUDE \"code.inc\"\nhalt",
            vfs);

        await Assert.That(model.Success).IsTrue();
        var sym = model.Symbols.FirstOrDefault(s => s.Name == "helper");
        await Assert.That(sym).IsNotNull();
    }

    [Test]
    public async Task Include_FileNotFound_Diagnostic()
    {
        var vfs = new VirtualFileResolver();
        var model = Emit("INCLUDE \"nonexistent.inc\"", vfs);
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("not found"))).IsTrue();
    }

    [Test]
    public async Task Include_CircularDetection()
    {
        var vfs = new VirtualFileResolver();
        vfs.AddTextFile("main.asm", "INCLUDE \"main.asm\"");

        var model = Emit("INCLUDE \"main.asm\"", vfs);
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("Circular"))).IsTrue();
    }

    [Test]
    public async Task Include_NoDiagnostics()
    {
        var vfs = new VirtualFileResolver();
        vfs.AddTextFile("defs.inc", "MY_VAL EQU $10");

        var model = Emit(
            "INCLUDE \"defs.inc\"\nSECTION \"Main\", ROM0\ndb MY_VAL",
            vfs);

        await Assert.That(model.Diagnostics).IsEmpty();
    }

    // =========================================================================
    // INCBIN
    // =========================================================================

    [Test]
    public async Task Incbin_EmbedsBytes()
    {
        var vfs = new VirtualFileResolver();
        vfs.AddBinaryFile("data.bin", [0xDE, 0xAD, 0xBE, 0xEF]);

        var model = Emit(
            "SECTION \"Main\", ROM0\nINCBIN \"data.bin\"",
            vfs);

        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(4);
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xDE);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0xAD);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0xBE);
        await Assert.That(model.Sections[0].Data[3]).IsEqualTo((byte)0xEF);
    }

    [Test]
    public async Task Incbin_FileNotFound_Diagnostic()
    {
        var vfs = new VirtualFileResolver();
        var model = Emit(
            "SECTION \"Main\", ROM0\nINCBIN \"missing.bin\"",
            vfs);
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("not found"))).IsTrue();
    }

    [Test]
    public async Task Incbin_LabelAfterIncbin_CorrectPC()
    {
        var vfs = new VirtualFileResolver();
        vfs.AddBinaryFile("data.bin", new byte[10]);

        var model = Emit(
            "SECTION \"Main\", ROM0\nINCBIN \"data.bin\"\nend_label:\nnop",
            vfs);

        await Assert.That(model.Success).IsTrue();
        var sym = model.Symbols.FirstOrDefault(s => s.Name == "end_label");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Value).IsEqualTo(10); // after 10 bytes of INCBIN
    }

    [Test]
    public async Task Incbin_OutsideSection_Diagnostic()
    {
        var vfs = new VirtualFileResolver();
        vfs.AddBinaryFile("data.bin", [0x00]);

        var model = Emit("INCBIN \"data.bin\"", vfs);
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("outside"))).IsTrue();
    }

    [Test]
    public async Task Include_NestedInclude_SymbolsVisible()
    {
        var vfs = new VirtualFileResolver();
        vfs.AddTextFile("a.inc", "INCLUDE \"b.inc\"");
        vfs.AddTextFile("b.inc", "DEEP_CONST EQU $99");

        var model = Emit(
            "INCLUDE \"a.inc\"\nSECTION \"Main\", ROM0\ndb DEEP_CONST",
            vfs);

        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x99);
    }

    [Test]
    public async Task Incbin_EmptyFile()
    {
        var vfs = new VirtualFileResolver();
        vfs.AddBinaryFile("empty.bin", []);

        var model = Emit(
            "SECTION \"Main\", ROM0\nINCBIN \"empty.bin\"\nend_label:\nnop",
            vfs);

        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data.Length).IsEqualTo(1); // only nop
    }

    // =========================================================================
    // Diagnostic FilePath attribution
    // =========================================================================

    [Test]
    public async Task Diagnostic_InIncludedFile_CarriesIncludedFilePath()
    {
        var vfs = new VirtualFileResolver();
        // The included file has an instruction outside a section → error
        vfs.AddTextFile("bad.asm", "nop");

        var model = Emit("INCLUDE \"bad.asm\"", vfs);

        await Assert.That(model.Success).IsFalse();
        var diag = model.Diagnostics.First(d => d.Message.Contains("outside of a section"));
        // FilePath must point to the included file, not the root
        await Assert.That(diag.FilePath).IsEqualTo("bad.asm");
    }

    [Test]
    public async Task Diagnostic_InRootFile_CarriesRootFilePath()
    {
        var model = Emit("nop"); // instruction outside section in root file

        await Assert.That(model.Success).IsFalse();
        var diag = model.Diagnostics.First(d => d.Message.Contains("outside of a section"));
        await Assert.That(diag.FilePath).IsEqualTo("main.asm");
    }
}
