namespace Koh.Compat.Tests;

/// <summary>
/// Integration tests that assemble with Koh, write RGBDS .o files,
/// and link with rgblink running inside a Testcontainers container.
/// Skipped when Docker is not available.
/// </summary>
public sealed class RgbdsLinkTests
{
    private readonly string _containerDir = "/work/" + Guid.NewGuid().ToString("N");

    [Before(Class)]
    public static async Task StartContainer()
    {
        try
        {
            await RgbdsCompatFixture.StartAsync();
        }
        catch (Exception ex)
        {
            Skip.Test($"Docker not available — cannot run RGBDS compat tests: {ex.Message}");
        }
    }

    [After(Class)]
    public static async Task StopContainer()
    {
        await RgbdsCompatFixture.StopAsync();
    }

    [Test]
    public async Task SimpleNop_LinksSuccessfully()
    {
        var model = RgbdsCompatFixture.Assemble("""
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();

        var objBytes = RgbdsCompatFixture.WriteObjectFile(model);
        var result = await RgbdsCompatFixture.LinkAsync(_containerDir, "test.gb",
            ("test.o", objBytes));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.RomData).IsNotNull();
    }

    [Test]
    public async Task DataBytes_PreservedInRom()
    {
        var model = RgbdsCompatFixture.Assemble("""
            SECTION "Main", ROM0[$0000]
            db $DE, $AD, $BE, $EF
            """);
        await Assert.That(model.Success).IsTrue();

        var objBytes = RgbdsCompatFixture.WriteObjectFile(model);
        var result = await RgbdsCompatFixture.LinkAsync(_containerDir, "data.gb",
            ("data.o", objBytes));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.RomData).IsNotNull();
        await Assert.That(result.RomData!.Length).IsGreaterThan(3);
        await Assert.That(result.RomData[0]).IsEqualTo((byte)0xDE);
        await Assert.That(result.RomData[1]).IsEqualTo((byte)0xAD);
        await Assert.That(result.RomData[2]).IsEqualTo((byte)0xBE);
        await Assert.That(result.RomData[3]).IsEqualTo((byte)0xEF);
    }

    [Test]
    public async Task FixedAddress_Respected()
    {
        var model = RgbdsCompatFixture.Assemble("""
            SECTION "Entry", ROM0[$0100]
            nop
            nop
            """);
        await Assert.That(model.Success).IsTrue();

        var objBytes = RgbdsCompatFixture.WriteObjectFile(model);
        var result = await RgbdsCompatFixture.LinkAsync(_containerDir, "fixed.gb",
            ("fixed.o", objBytes));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.RomData).IsNotNull();
        await Assert.That(result.RomData!.Length).IsGreaterThan(0x0101);
        await Assert.That(result.RomData[0x0100]).IsEqualTo((byte)0x00);
        await Assert.That(result.RomData[0x0101]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task MultipleInstructions_CorrectEncoding()
    {
        var model = RgbdsCompatFixture.Assemble("""
            SECTION "Main", ROM0[$0000]
            nop
            ld a, b
            halt
            """);
        await Assert.That(model.Success).IsTrue();

        var objBytes = RgbdsCompatFixture.WriteObjectFile(model);
        var result = await RgbdsCompatFixture.LinkAsync(_containerDir, "multi.gb",
            ("multi.o", objBytes));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.RomData).IsNotNull();
        await Assert.That(result.RomData!.Length).IsGreaterThan(2);
        await Assert.That(result.RomData[0]).IsEqualTo((byte)0x00); // nop
        await Assert.That(result.RomData[1]).IsEqualTo((byte)0x78); // ld a, b
        await Assert.That(result.RomData[2]).IsEqualTo((byte)0x76); // halt
    }

    [Test]
    public async Task ExportedSymbol_VisibleToLinker()
    {
        var model = RgbdsCompatFixture.Assemble("""
            SECTION "Main", ROM0
            main:: nop
            """);
        await Assert.That(model.Success).IsTrue();

        var objBytes = RgbdsCompatFixture.WriteObjectFile(model);
        var result = await RgbdsCompatFixture.LinkAsync(_containerDir, "export.gb",
            ("export.o", objBytes));

        await Assert.That(result.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task LargerProgram_LinksSuccessfully()
    {
        var model = RgbdsCompatFixture.Assemble("""
            SECTION "Main", ROM0
            start::
                ld a, $42
                ld b, a
                add a, b
                cp $84
                nop
                halt
            """);
        await Assert.That(model.Success).IsTrue();

        var objBytes = RgbdsCompatFixture.WriteObjectFile(model);
        var result = await RgbdsCompatFixture.LinkAsync(_containerDir, "program.gb",
            ("program.o", objBytes));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.RomData).IsNotNull();
    }

    // =========================================================================
    // Mixed Koh + rgbasm object files
    // =========================================================================

    [Test]
    public async Task MixedKohAndRgbasm_LinkTogether()
    {
        // File 1: assembled by Koh — non-zero bytes for meaningful assertions
        var kohModel = RgbdsCompatFixture.Assemble("""
            SECTION "KohCode", ROM0[$0000]
            koh_entry::
                db $DE, $AD, $BE
            """);
        await Assert.That(kohModel.Success).IsTrue();
        var kohObjBytes = RgbdsCompatFixture.WriteObjectFile(kohModel);

        // File 2: assembled by rgbasm — non-zero bytes
        var rgbasmObjBytes = await RgbdsCompatFixture.RgbasmAssembleAsync("""
            SECTION "RgbasmCode", ROM0[$0010]
            rgbasm_entry::
                db $CA, $FE
            """, _containerDir, "rgbasm_part");

        if (rgbasmObjBytes == null)
            Skip.Test("rgbasm not available or failed to assemble");

        // Link both together with rgblink
        var result = await RgbdsCompatFixture.LinkAsync(_containerDir, "mixed.gb",
            ("koh_part.o", kohObjBytes),
            ("rgbasm_part.o", rgbasmObjBytes!));

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.RomData).IsNotNull();
        await Assert.That(result.RomData!.Length).IsGreaterThan(0x11);
        // Koh section at $0000: $DE $AD $BE
        await Assert.That(result.RomData[0x0000]).IsEqualTo((byte)0xDE);
        await Assert.That(result.RomData[0x0001]).IsEqualTo((byte)0xAD);
        await Assert.That(result.RomData[0x0002]).IsEqualTo((byte)0xBE);
        // rgbasm section at $0010: $CA $FE
        await Assert.That(result.RomData[0x0010]).IsEqualTo((byte)0xCA);
        await Assert.That(result.RomData[0x0011]).IsEqualTo((byte)0xFE);
    }
}
