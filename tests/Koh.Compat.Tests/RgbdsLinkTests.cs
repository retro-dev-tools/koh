namespace Koh.Compat.Tests;

/// <summary>
/// Integration tests that assemble with Koh, write RGBDS .o files,
/// and link with the real rgblink tool. Skipped when rgblink is not available.
/// Run via: docker compose run --rm --build compat-tests
/// </summary>
public sealed class RgbdsLinkTests : IDisposable
{
    private readonly string _tmpDir;

    public RgbdsLinkTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "koh-compat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    private static void SkipIfNoRgblink()
    {
        if (!RgbdsCompatFixture.IsAvailable)
            Skip.Test("rgblink not available — run: docker compose run --rm --build compat-tests");
    }

    [Test]
    public async Task SimpleNop_LinksSuccessfully()
    {
        SkipIfNoRgblink();

        var model = RgbdsCompatFixture.Assemble("""
            SECTION "Main", ROM0
            nop
            """);
        await Assert.That(model.Success).IsTrue();

        var objPath = RgbdsCompatFixture.WriteObjectFile(model, _tmpDir, "test.o");
        var romPath = Path.Combine(_tmpDir, "test.gb");
        var result = await RgbdsCompatFixture.LinkAsync(romPath, objPath);

        Console.WriteLine($"rgblink exit: {result.ExitCode}");
        if (!string.IsNullOrEmpty(result.Stderr))
            Console.WriteLine($"rgblink stderr: {result.Stderr}");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.RomData).IsNotNull();
    }

    [Test]
    public async Task DataBytes_PreservedInRom()
    {
        SkipIfNoRgblink();

        var model = RgbdsCompatFixture.Assemble("""
            SECTION "Main", ROM0[$0000]
            db $DE, $AD, $BE, $EF
            """);
        await Assert.That(model.Success).IsTrue();

        var objPath = RgbdsCompatFixture.WriteObjectFile(model, _tmpDir, "data.o");
        var romPath = Path.Combine(_tmpDir, "data.gb");
        var result = await RgbdsCompatFixture.LinkAsync(romPath, objPath);

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
        SkipIfNoRgblink();

        var model = RgbdsCompatFixture.Assemble("""
            SECTION "Entry", ROM0[$0100]
            nop
            nop
            """);
        await Assert.That(model.Success).IsTrue();

        var objPath = RgbdsCompatFixture.WriteObjectFile(model, _tmpDir, "fixed.o");
        var romPath = Path.Combine(_tmpDir, "fixed.gb");
        var result = await RgbdsCompatFixture.LinkAsync(romPath, objPath);

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.RomData).IsNotNull();
        await Assert.That(result.RomData!.Length).IsGreaterThan(0x0101);
        await Assert.That(result.RomData[0x0100]).IsEqualTo((byte)0x00);
        await Assert.That(result.RomData[0x0101]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task MultipleInstructions_CorrectEncoding()
    {
        SkipIfNoRgblink();

        var model = RgbdsCompatFixture.Assemble("""
            SECTION "Main", ROM0[$0000]
            nop
            ld a, b
            halt
            """);
        await Assert.That(model.Success).IsTrue();

        var objPath = RgbdsCompatFixture.WriteObjectFile(model, _tmpDir, "multi.o");
        var romPath = Path.Combine(_tmpDir, "multi.gb");
        var result = await RgbdsCompatFixture.LinkAsync(romPath, objPath);

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
        SkipIfNoRgblink();

        var model = RgbdsCompatFixture.Assemble("""
            SECTION "Main", ROM0
            main:: nop
            """);
        await Assert.That(model.Success).IsTrue();

        var objPath = RgbdsCompatFixture.WriteObjectFile(model, _tmpDir, "export.o");
        var romPath = Path.Combine(_tmpDir, "export.gb");
        var result = await RgbdsCompatFixture.LinkAsync(romPath, objPath);

        await Assert.That(result.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task LargerProgram_LinksSuccessfully()
    {
        SkipIfNoRgblink();

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

        var objPath = RgbdsCompatFixture.WriteObjectFile(model, _tmpDir, "program.o");
        var romPath = Path.Combine(_tmpDir, "program.gb");
        var result = await RgbdsCompatFixture.LinkAsync(romPath, objPath);

        Console.WriteLine($"rgblink exit: {result.ExitCode}");
        if (!string.IsNullOrEmpty(result.Stderr))
            Console.WriteLine($"rgblink stderr: {result.Stderr}");

        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.RomData).IsNotNull();
    }
}
