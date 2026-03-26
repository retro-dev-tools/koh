using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Linker.Core;

namespace Koh.Linker.Tests;

public class LinkerTests
{
    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    private static LinkerInput Input(string source, string filePath = "test.asm") =>
        new(filePath, Emit(source));

    private static LinkResult LinkSingle(string source) =>
        new Koh.Linker.Core.Linker().Link([Input(source)]);

    // =========================================================================
    // Basic linking
    // =========================================================================

    [Test]
    public async Task Link_SingleFile_ProducesRom()
    {
        var result = LinkSingle("SECTION \"Main\", ROM0\nnop\nhalt");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData).IsNotNull();
        await Assert.That(result.RomData!.Length).IsGreaterThanOrEqualTo(0x8000); // min 32KB
    }

    [Test]
    public async Task Link_SectionPlacedAtCorrectAddress()
    {
        var result = LinkSingle("SECTION \"Main\", ROM0\nnop\nhalt");
        await Assert.That(result.Success).IsTrue();
        // Section data starts at 0x0000 (ROM0 base)
        await Assert.That(result.RomData![0]).IsEqualTo((byte)0x00); // nop
        await Assert.That(result.RomData![1]).IsEqualTo((byte)0x76); // halt
    }

    [Test]
    public async Task Link_HeaderChecksum_Correct()
    {
        var result = LinkSingle("SECTION \"Main\", ROM0\nnop");
        await Assert.That(result.Success).IsTrue();
        // Verify header checksum at $014D
        byte expected = 0;
        for (int i = 0x0134; i <= 0x014C; i++)
            expected = (byte)(expected - result.RomData![i] - 1);
        await Assert.That(result.RomData![0x014D]).IsEqualTo(expected);
    }

    [Test]
    public async Task Link_GlobalChecksum_Correct()
    {
        var result = LinkSingle("SECTION \"Main\", ROM0\nnop");
        await Assert.That(result.Success).IsTrue();
        ushort expected = 0;
        for (int i = 0; i < result.RomData!.Length; i++)
        {
            if (i == 0x014E || i == 0x014F) continue;
            expected += result.RomData[i];
        }
        await Assert.That(result.RomData[0x014E]).IsEqualTo((byte)(expected >> 8));
        await Assert.That(result.RomData[0x014F]).IsEqualTo((byte)(expected & 0xFF));
    }

    // =========================================================================
    // Symbol resolution
    // =========================================================================

    [Test]
    public async Task Link_ExportedSymbol_Resolved()
    {
        var result = LinkSingle("SECTION \"Main\", ROM0\nmain::\nnop");
        await Assert.That(result.Success).IsTrue();
        var sym = result.Symbols.FirstOrDefault(s => s.Name == "main");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.AbsoluteAddress).IsEqualTo(0);
    }

    [Test]
    public async Task Link_SymbolAfterInstruction_CorrectAddress()
    {
        var result = LinkSingle("SECTION \"Main\", ROM0\nnop\nnop\nend::\nnop");
        var sym = result.Symbols.FirstOrDefault(s => s.Name == "end");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.AbsoluteAddress).IsEqualTo(2); // after 2 NOPs
    }

    [Test]
    public async Task Link_DuplicateExport_Diagnostic()
    {
        var input1 = Input("SECTION \"A\", ROM0\nmain::\nnop", "a.asm");
        var input2 = Input("SECTION \"B\", ROM0\nmain::\nnop", "b.asm");
        var result = new Koh.Linker.Core.Linker().Link([input1, input2]);

        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("Duplicate"))).IsTrue();
    }

    // =========================================================================
    // Section placement
    // =========================================================================

    [Test]
    public async Task Link_TwoSections_BothPlaced()
    {
        var result = LinkSingle(
            "SECTION \"Code\", ROM0\nnop\nnop\nSECTION \"Data\", ROM0\ndb $42");
        await Assert.That(result.Success).IsTrue();
        // Code at 0, Data immediately after
        await Assert.That(result.RomData![0]).IsEqualTo((byte)0x00); // nop
        await Assert.That(result.RomData![1]).IsEqualTo((byte)0x00); // nop
        await Assert.That(result.RomData![2]).IsEqualTo((byte)0x42); // db $42
    }

    [Test]
    public async Task Link_ConstantSymbol_ValuePreserved()
    {
        var result = LinkSingle("MY_CONST EQU $42\nSECTION \"Main\", ROM0\nnop");
        var sym = result.Symbols.FirstOrDefault(s => s.Name == "MY_CONST");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.AbsoluteAddress).IsEqualTo(0x42);
    }

    // =========================================================================
    // ROM size
    // =========================================================================

    [Test]
    public async Task Link_MinimumRomSize_32KB()
    {
        var result = LinkSingle("SECTION \"Main\", ROM0\nnop");
        await Assert.That(result.RomData!.Length).IsEqualTo(0x8000);
    }

    // =========================================================================
    // .sym file
    // =========================================================================

    [Test]
    public async Task SymFile_ContainsSymbols()
    {
        var result = LinkSingle("SECTION \"Main\", ROM0\nmain::\nnop\nend::\nhalt");
        using var writer = new StringWriter();
        SymFileWriter.Write(writer, result.Symbols);
        var output = writer.ToString();

        await Assert.That(output).Contains("00:0000 main");
        await Assert.That(output).Contains("00:0001 end");
    }

    // =========================================================================
    // Multi-file linking
    // =========================================================================

    [Test]
    public async Task Link_MultipleFiles_SectionsPlaced()
    {
        var input1 = Input("SECTION \"Code\", ROM0\nnop\nnop", "code.asm");
        var input2 = Input("SECTION \"Data\", ROM0\ndb $FF", "data.asm");
        var result = new Koh.Linker.Core.Linker().Link([input1, input2]);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData![0]).IsEqualTo((byte)0x00); // nop from code.asm
        await Assert.That(result.RomData![1]).IsEqualTo((byte)0x00); // nop from code.asm
        await Assert.That(result.RomData![2]).IsEqualTo((byte)0xFF); // db from data.asm
    }

    [Test]
    public async Task Link_NoDiagnostics_CleanProgram()
    {
        var result = LinkSingle(
            "MY_CONST EQU $10\nSECTION \"Main\", ROM0\nmain::\nld a, MY_CONST\nhalt");
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.Success).IsTrue();
    }

    // =========================================================================
    // ROMX banked ROM
    // =========================================================================

    [Test]
    public async Task Link_RomxSection_PlacedInPhysicalBankOffset()
    {
        // A floating ROMX section should be placed at physical offset 0x4000 (bank 1)
        // in the flat ROM image, not at GB virtual address 0x4000 which would overlap
        // with bank 0 data if multiple banks are present.
        var input1 = Input("SECTION \"Bank0\", ROM0\ndb $11", "rom0.asm");
        var input2 = Input("SECTION \"Bank1\", ROMX\ndb $22", "romx.asm");
        var result = new Koh.Linker.Core.Linker().Link([input1, input2]);

        await Assert.That(result.Success).IsTrue();
        // Bank 0 data at physical offset 0x0000
        await Assert.That(result.RomData![0x0000]).IsEqualTo((byte)0x11);
        // Bank 1 data at physical offset 0x4000 (bank 1 * 0x4000 + (0x4000 - 0x4000))
        await Assert.That(result.RomData![0x4000]).IsEqualTo((byte)0x22);
    }

    [Test]
    public async Task Link_RomxSection_RomSizedCorrectly()
    {
        // A ROM with one ROM0 section and one ROMX section must be at least 2 banks = 0x8000 bytes.
        var input1 = Input("SECTION \"Boot\", ROM0\nnop", "rom0.asm");
        var input2 = Input("SECTION \"Banked\", ROMX\ndb $FF", "romx.asm");
        var result = new Koh.Linker.Core.Linker().Link([input1, input2]);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData!.Length).IsGreaterThanOrEqualTo(0x8000);
    }

    [Test]
    public async Task Link_RomxSection_SymbolHasCorrectBank()
    {
        // A symbol in a floating ROMX section should report bank 1 in the sym file.
        var result = LinkSingle("SECTION \"Banked\", ROMX\nfunc::\nnop");
        var sym = result.Symbols.FirstOrDefault(s => s.Name == "func");
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.PlacedBank).IsEqualTo(1);

        // Verify the sym file also emits the correct bank prefix.
        using var writer = new StringWriter();
        SymFileWriter.Write(writer, result.Symbols);
        await Assert.That(writer.ToString()).Contains("01:4000 func");
    }

    [Test]
    public async Task Link_TwoRomxBanks_DataAtCorrectPhysicalOffsets()
    {
        // Two ROMX sections pinned to banks 1 and 2 should land at physical offsets
        // 0x4000 and 0x8000 respectively, not both at 0x4000.
        // BANK[$01] / BANK[$02]: the assembler syntax uses hex literals in brackets.
        var input1 = Input("SECTION \"Bank1\", ROMX[$4000], BANK[$01]\ndb $AA", "b1.asm");
        var input2 = Input("SECTION \"Bank2\", ROMX[$4000], BANK[$02]\ndb $BB", "b2.asm");
        var result = new Koh.Linker.Core.Linker().Link([input1, input2]);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData![0x4000]).IsEqualTo((byte)0xAA); // bank 1 physical offset
        await Assert.That(result.RomData![0x8000]).IsEqualTo((byte)0xBB); // bank 2 physical offset
    }

    // =========================================================================
    // Fixed-address placement
    // =========================================================================

    [Test]
    public async Task Link_FixedAddress_PlacedCorrectly()
    {
        var result = LinkSingle("SECTION \"Entry\", ROM0[$0100]\nnop\nhalt");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RomData![0x0100]).IsEqualTo((byte)0x00); // nop
        await Assert.That(result.RomData![0x0101]).IsEqualTo((byte)0x76); // halt
    }

    // =========================================================================
    // Section-does-not-fit error
    // =========================================================================

    [Test]
    public async Task Link_SectionTooLarge_Diagnostic()
    {
        // HRAM is $FF80-$FFFE = 126 bytes. A 200-byte section should not fit.
        var ds200 = "ds 200";
        var result = LinkSingle($"SECTION \"Big\", HRAM\n{ds200}");
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.RomData).IsNull();
        await Assert.That(result.Diagnostics.Any(d => d.Message.Contains("does not fit"))).IsTrue();
    }
}
