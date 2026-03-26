using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Emit;

namespace Koh.Emit.Tests;

public class KobjRoundtripTests
{
    private static EmitModel RoundTrip(EmitModel model)
    {
        using var ms = new MemoryStream();
        KobjWriter.Write(ms, model);
        ms.Position = 0;
        return KobjReader.Read(ms);
    }

    private static EmitModel EmitFromSource(string source)
    {
        var tree = SyntaxTree.Parse(source);
        var compilation = Compilation.Create(tree);
        return compilation.Emit();
    }

    [Test]
    public async Task Roundtrip_SimpleProgram()
    {
        var original = EmitFromSource("SECTION \"Main\", ROM0\nnop\nhalt");
        var restored = RoundTrip(original);

        await Assert.That(restored.Sections.Count).IsEqualTo(1);
        await Assert.That(restored.Sections[0].Name).IsEqualTo("Main");
        await Assert.That(restored.Sections[0].Type).IsEqualTo(SectionType.Rom0);
        await Assert.That(restored.Sections[0].Data.Length).IsEqualTo(2);
        await Assert.That(restored.Sections[0].Data[0]).IsEqualTo((byte)0x00);
        await Assert.That(restored.Sections[0].Data[1]).IsEqualTo((byte)0x76);
    }

    [Test]
    public async Task Roundtrip_Symbols()
    {
        var original = EmitFromSource(
            "MY_CONST EQU $42\nSECTION \"Main\", ROM0\nmain::\nnop");
        var restored = RoundTrip(original);

        var constSym = restored.Symbols.FirstOrDefault(s => s.Name == "MY_CONST");
        await Assert.That(constSym).IsNotNull();
        await Assert.That(constSym!.Kind).IsEqualTo(SymbolKind.Constant);
        await Assert.That(constSym.Value).IsEqualTo(0x42);
        await Assert.That(constSym.Visibility).IsEqualTo(SymbolVisibility.Local);

        var mainSym = restored.Symbols.FirstOrDefault(s => s.Name == "main");
        await Assert.That(mainSym).IsNotNull();
        await Assert.That(mainSym!.Kind).IsEqualTo(SymbolKind.Label);
        await Assert.That(mainSym.Visibility).IsEqualTo(SymbolVisibility.Exported);
        await Assert.That(mainSym.Section).IsEqualTo("Main");
    }

    [Test]
    public async Task Roundtrip_MultipleSections()
    {
        var original = EmitFromSource(
            "SECTION \"Code\", ROM0\nnop\nSECTION \"Data\", ROM0\ndb $AA, $BB");
        var restored = RoundTrip(original);

        await Assert.That(restored.Sections.Count).IsEqualTo(2);
        var code = restored.Sections.First(s => s.Name == "Code");
        var data = restored.Sections.First(s => s.Name == "Data");
        await Assert.That(code.Data.Length).IsEqualTo(1);
        await Assert.That(data.Data.Length).IsEqualTo(2);
        await Assert.That(data.Data[0]).IsEqualTo((byte)0xAA);
        await Assert.That(data.Data[1]).IsEqualTo((byte)0xBB);
    }

    [Test]
    public async Task Roundtrip_SectionType()
    {
        var original = EmitFromSource("SECTION \"Work\", WRAM0\nds 2");
        var restored = RoundTrip(original);

        await Assert.That(restored.Sections[0].Type).IsEqualTo(SectionType.Wram0);
    }

    [Test]
    public async Task Roundtrip_EmptyProgram()
    {
        var original = EmitFromSource("SECTION \"Empty\", ROM0");
        var restored = RoundTrip(original);

        await Assert.That(restored.Sections.Count).IsEqualTo(1);
        await Assert.That(restored.Sections[0].Data.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Roundtrip_SymbolCount()
    {
        // Use non-register names — A/B/C are lexed as register keywords and would fail to
        // parse as EQU symbol names (pre-existing parser limitation, tracked separately).
        var original = EmitFromSource(
            "FOO EQU 1\nBAR EQU 2\nBAZ EQU 3\nSECTION \"Main\", ROM0\nlbl:\nnop");
        var restored = RoundTrip(original);

        await Assert.That(restored.Symbols.Count).IsEqualTo(original.Symbols.Count);
    }

    [Test]
    public async Task Roundtrip_LargeData()
    {
        // 256 bytes of data
        var dbLine = "db " + string.Join(", ", Enumerable.Range(0, 256).Select(i => $"${i:X2}"));
        var original = EmitFromSource($"SECTION \"Big\", ROM0\n{dbLine}");
        var restored = RoundTrip(original);

        await Assert.That(restored.Sections[0].Data.Length).IsEqualTo(256);
        await Assert.That(restored.Sections[0].Data[0]).IsEqualTo((byte)0x00);
        await Assert.That(restored.Sections[0].Data[255]).IsEqualTo((byte)0xFF);
    }

    [Test]
    public void InvalidMagic_Throws()
    {
        var ms = new MemoryStream(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        Assert.Throws<InvalidDataException>(() => KobjReader.Read(ms));
    }

    [Test]
    public void InvalidVersion_Throws()
    {
        // Write a valid magic + unsupported version byte, then nothing else.
        // Reader must reject it before attempting to parse any tags.
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write("KOH\0"u8);
        bw.Write((byte)0xFF); // version 255 — not version 1
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => KobjReader.Read(ms));
    }

    [Test]
    public async Task Write_FailedModel_Throws()
    {
        // An instruction outside any SECTION is a guaranteed binder error → Success=false.
        // KobjWriter must refuse to write rather than silently producing a file that would
        // be read back as Success=true (diagnostics are not serialized in .kobj).
        var model = EmitFromSource("nop");
        await Assert.That(model.Success).IsFalse();

        using var ms = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => KobjWriter.Write(ms, model));
    }

    [Test]
    public async Task Roundtrip_DeserializedSuccess_IsTrue()
    {
        var original = EmitFromSource("SECTION \"Main\", ROM0\nnop");
        await Assert.That(original.Success).IsTrue();
        var restored = RoundTrip(original);
        await Assert.That(restored.Success).IsTrue();
    }

    [Test]
    public void UnknownTag_Throws()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        bw.Write("KOH\0"u8);
        bw.Write((byte)1); // valid version
        bw.Write((byte)0xAB); // unknown tag
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => KobjReader.Read(ms));
    }

    [Test]
    public async Task Roundtrip_Patch()
    {
        // Build an EmitModel with an unresolved patch directly
        var patchEntry = new PatchEntry
        {
            SectionName = "Main",
            Offset = 3,
            Expression = null, // linker-time patch
            Kind = PatchKind.Absolute16,
            PCAfterInstruction = 5,
            DiagnosticSpan = new TextSpan(10, 4),
        };
        var sectionData = new SectionData("Main", SectionType.Rom0, null, null,
            new byte[] { 0x00, 0xC3, 0x00, 0x00 }, [patchEntry]);
        var original = new EmitModel([sectionData], [], success: true);
        var restored = RoundTrip(original);

        await Assert.That(restored.Sections[0].Patches.Count).IsEqualTo(1);
        var patch = restored.Sections[0].Patches[0];
        await Assert.That(patch.Offset).IsEqualTo(3);
        await Assert.That(patch.Kind).IsEqualTo(PatchKind.Absolute16);
        await Assert.That(patch.PCAfterInstruction).IsEqualTo(5);
        await Assert.That(patch.DiagnosticSpan.Start).IsEqualTo(10);
        await Assert.That(patch.DiagnosticSpan.Length).IsEqualTo(4);
    }

    [Test]
    public async Task Roundtrip_ConstantSection_IsNull()
    {
        var original = EmitFromSource("MY_CONST EQU $42\nSECTION \"Main\", ROM0\nnop");
        var restored = RoundTrip(original);

        var constSym = restored.Symbols.FirstOrDefault(s => s.Name == "MY_CONST");
        await Assert.That(constSym).IsNotNull();
        // Constants have no section — null must survive the "" roundtrip
        await Assert.That(constSym!.Section).IsNull();
    }
}
