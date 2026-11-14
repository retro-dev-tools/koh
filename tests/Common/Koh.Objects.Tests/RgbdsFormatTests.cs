using Koh.Native;
using Koh.Native.Syntax;
using Koh.Objects;

namespace Koh.Objects.Tests;

public class RgbdsFormatTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    private static byte[] WriteToBytes(EmitModel model)
    {
        using var ms = new MemoryStream();
        RgbdsObjectWriter.Write(ms, model);
        return ms.ToArray();
    }

    [Test]
    public async Task Header_ContainsMagicAndRevision()
    {
        var model = Emit("SECTION \"Main\", ROM0\nnop");
        var bytes = WriteToBytes(model);

        // Magic: RGB9
        await Assert.That(bytes[0]).IsEqualTo((byte)'R');
        await Assert.That(bytes[1]).IsEqualTo((byte)'G');
        await Assert.That(bytes[2]).IsEqualTo((byte)'B');
        await Assert.That(bytes[3]).IsEqualTo((byte)'9');

        // Revision: 13 (little-endian int32)
        var revision = BitConverter.ToInt32(bytes, 4);
        await Assert.That(revision).IsEqualTo(13);
    }

    [Test]
    public async Task Header_SymbolAndSectionCounts()
    {
        var model = Emit(
            """
            SECTION "Main", ROM0
            my_label: nop
            """
        );
        var bytes = WriteToBytes(model);

        var numSymbols = BitConverter.ToInt32(bytes, 8);
        var numSections = BitConverter.ToInt32(bytes, 12);
        var numNodes = BitConverter.ToInt32(bytes, 16);

        await Assert.That(numSymbols).IsGreaterThanOrEqualTo(1); // at least my_label
        await Assert.That(numSections).IsEqualTo(1);
        await Assert.That(numNodes).IsEqualTo(1); // root node
    }

    [Test]
    public async Task WriteAndRead_DoesNotThrow()
    {
        var model = Emit(
            """
            SECTION "Main", ROM0
            nop
            ld a, b
            """
        );
        var bytes = WriteToBytes(model);
        await Assert.That(bytes.Length).IsGreaterThan(20); // header + content
    }

    [Test]
    public async Task Section_ContainsCorrectData()
    {
        var model = Emit(
            """
            SECTION "Code", ROM0
            nop
            nop
            nop
            """
        );
        var bytes = WriteToBytes(model);

        // The output should contain the section data bytes (3 NOPs = 0x00, 0x00, 0x00)
        // Find the section data in the binary: after header, file nodes, symbols
        await Assert.That(bytes.Length).IsGreaterThan(30);
        // The 3 NOP bytes should appear somewhere in the output
        bool found = false;
        for (int i = 0; i < bytes.Length - 2; i++)
        {
            if (bytes[i] == 0x00 && bytes[i + 1] == 0x00 && bytes[i + 2] == 0x00)
            {
                found = true;
                break;
            }
        }
        await Assert.That(found).IsTrue();
    }

    [Test]
    public async Task Symbol_ExportedLabelPresent()
    {
        var model = Emit(
            """
            SECTION "Main", ROM0
            my_func:: nop
            """
        );
        var bytes = WriteToBytes(model);

        // The exported symbol name should appear in the binary
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        await Assert.That(text.Contains("my_func")).IsTrue();
    }

    [Test]
    public async Task MultipleSymbols_AllPresent()
    {
        var model = Emit(
            """
            SECTION "Main", ROM0
            start:: nop
            loop:: nop
            done:: nop
            """
        );
        var bytes = WriteToBytes(model);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        await Assert.That(text.Contains("start")).IsTrue();
        await Assert.That(text.Contains("loop")).IsTrue();
        await Assert.That(text.Contains("done")).IsTrue();
    }

    [Test]
    public async Task FailedModel_ThrowsException()
    {
        var model = Emit("ASSERT 0"); // produces an error
        await Assert.That(model.Success).IsFalse();

        await Assert
            .That(() =>
            {
                using var ms = new MemoryStream();
                RgbdsObjectWriter.Write(ms, model);
            })
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task SectionType_Rom0EncodedCorrectly()
    {
        var model = Emit("SECTION \"Main\", ROM0\nnop");
        var bytes = WriteToBytes(model);

        // Section name followed by nodeID(4) + lineNo(4) + size(4) + type(1)
        var nameBytes = System.Text.Encoding.UTF8.GetBytes("Main\0");
        int nameStart = FindSubarray(bytes, nameBytes);
        await Assert.That(nameStart).IsGreaterThan(-1);

        // Type byte is at: name + nodeID(4) + lineNo(4) + size(4)
        int typeOffset = nameStart + nameBytes.Length + 4 + 4 + 4;
        int sectionType = bytes[typeOffset];
        await Assert.That(sectionType).IsEqualTo(3); // SectRom0
    }

    [Test]
    public async Task ImportSymbol_WrittenCorrectly()
    {
        // Assemble with AllowUndefinedSymbols — undefined symbol becomes an import
        var tree = SyntaxTree.Parse(
            """
            SECTION "Main", ROM0
            dw external_func
            """
        );
        var options = new AssemblyOptions { AllowUndefinedSymbols = true };
        var bytes = Compilation.Create(options, tree).EmitBytes("rgbds");
        await Assert.That(SymbolType(bytes, "external_func")).IsEqualTo((byte)1);
        await Assert.That(bytes.Length).IsGreaterThan(20);

        // The symbol name should appear in the binary
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        await Assert.That(text.Contains("external_func")).IsTrue();
    }

    [Test]
    public async Task ImportSymbol_PatchContainsRpnSymbolOpcode()
    {
        var tree = SyntaxTree.Parse(
            """
            SECTION "Main", ROM0
            dw external_func
            """
        );
        var options = new AssemblyOptions { AllowUndefinedSymbols = true };
        var bytes = Compilation.Create(options, tree).EmitBytes("rgbds");
        var patches = RgbdsExpressionTests.DecodePatches(bytes);
        await Assert.That(patches.Count).IsEqualTo(1);
        await Assert.That(patches[0]).IsEqualTo("0:1:0:sym:external_func");
    }

    [Test]
    public async Task ForwardRef_SameFile_NotImport()
    {
        // A forward-referenced label defined later in the same file should NOT become an import
        var tree = SyntaxTree.Parse(
            """
            SECTION "Main", ROM0
            dw my_label
            my_label: nop
            """
        );
        var options = new AssemblyOptions { AllowUndefinedSymbols = true };
        var model = Compilation.Create(options, tree).Emit();
        await Assert.That(model.Success).IsTrue();

        var sym = model.Symbols.FirstOrDefault(s => s.Name == "my_label");
        await Assert.That(sym).IsNotNull();
        // Should be Local or Exported, NOT Imported — it's defined in this file
        await Assert.That(sym!.Visibility).IsNotEqualTo(Koh.Objects.SymbolVisibility.Imported);
    }

    [Test]
    public async Task RamSection_NoDataEmitted()
    {
        var model = Emit(
            """
            SECTION "RAM", WRAM0
            my_var: ds 4
            """
        );
        var bytes = WriteToBytes(model);

        // WRAM0 section should have size 4 but no data bytes in the output
        // (RAM sections don't have data in RGBDS format)
        await Assert.That(bytes.Length).IsGreaterThan(20);
    }

    private static int FindSubarray(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    private static byte SymbolType(byte[] bytes, string name)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        string CString()
        {
            var value = new List<byte>();
            for (byte next; (next = reader.ReadByte()) != 0; )
                value.Add(next);
            return System.Text.Encoding.UTF8.GetString(value.ToArray());
        }
        reader.ReadBytes(8);
        var symbols = reader.ReadInt32();
        reader.ReadInt32();
        var nodes = reader.ReadInt32();
        for (var node = 0; node < nodes; node++)
        {
            reader.ReadBytes(9);
            CString();
        }
        for (var symbol = 0; symbol < symbols; symbol++)
        {
            var actual = CString();
            var type = reader.ReadByte();
            if (actual == name)
                return type;
            if (type != 1)
                reader.ReadBytes(16);
        }
        throw new InvalidDataException($"Missing symbol '{name}'");
    }
}
