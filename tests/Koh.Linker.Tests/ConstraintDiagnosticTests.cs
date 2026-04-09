using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Linker.Core;

namespace Koh.Linker.Tests;

public class ConstraintDiagnosticTests
{
    [Test]
    public async Task Placement_SingleSectionTooLarge_ReportsSizeAndCapacity()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[] { CreateSection("huge", SectionType.Rom0, size: 0x5000) });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("huge");
        await Assert.That(errors[0].Message).Contains("20480");
        await Assert.That(errors[0].Message).Contains("16384");
    }

    [Test]
    public async Task Placement_TwoSectionsExceedBank_ReportsFailingSection()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("data_a", SectionType.Rom0, size: 0x3000),
            CreateSection("data_b", SectionType.Rom0, size: 0x2000),
        });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("data_b");
    }

    [Test]
    public async Task Placement_FixedBankOverflow_ReportsBankNumber()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("code_a", SectionType.RomX, size: 0x3800, bank: 1),
            CreateSection("code_b", SectionType.RomX, size: 0x1000, bank: 1),
        });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("bank 1");
    }

    [Test]
    public async Task Placement_OverflowAmount_Reported()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        // ROM0 = 0x4000 (16384) capacity.
        // A=0x3000 (12288), B=0xC00 (3072) -> used=0x3C00 (15360), remaining=0x400 (1024 free)
        // C=0x600 (1536) needs 1536 but only 1024 free -> overflow = 1536 - 1024 = 512
        // overflow (512) != free space (1024), so both values are independently testable
        placer.PlaceAll(new[]
        {
            CreateSection("A", SectionType.Rom0, size: 0x3000),
            CreateSection("B", SectionType.Rom0, size: 0xC00),
            CreateSection("C", SectionType.Rom0, size: 0x600),
        });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("C");
        await Assert.That(errors[0].Message).Contains("1536");   // section size
        await Assert.That(errors[0].Message).Contains("1024");   // largest free space
        await Assert.That(errors[0].Message).Contains("512");    // overflow amount (distinct from free space)
    }

    [Test]
    public async Task Placement_FixedAddressOverlap_ReportsBothSections()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("vectors", SectionType.Rom0, size: 0x100, fixedAddress: 0x0000),
            CreateSection("overlap", SectionType.Rom0, size: 0x100, fixedAddress: 0x0080),
        });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("overlap");
        await Assert.That(errors[0].Message).Contains("vectors");
    }

    [Test]
    public async Task Placement_FixedAddressOverlap_EmittedOnceNotTwice()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("first", SectionType.Rom0, size: 0x100, fixedAddress: 0x0000),
            CreateSection("second", SectionType.Rom0, size: 0x100, fixedAddress: 0x0080),
        });

        var overlapErrors = diags.ToList()
            .Where(d => d.Message.Contains("overlaps"))
            .ToList();
        await Assert.That(overlapErrors.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Placement_FloatingFitsElsewhere_NoError()
    {
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("A", SectionType.RomX, size: 0x3000),
            CreateSection("B", SectionType.RomX, size: 0x3000),
        });

        await Assert.That(diags.ToList().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Placement_NoRoomAnywhere_ReportsLargestFreeSpace()
    {
        // ROM0: single bank, 0x4000 capacity
        // fill = 0x3800 -> remaining 0x800 (2048)
        // toobig = 0x1000 (4096) won't fit
        var diags = new DiagnosticBag();
        var placer = new SectionPlacer(diags);
        placer.PlaceAll(new[]
        {
            CreateSection("fill", SectionType.Rom0, size: 0x3800),
            CreateSection("toobig", SectionType.Rom0, size: 0x1000),
        });

        var errors = diags.ToList();
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains("toobig");
        await Assert.That(errors[0].Message).Contains("free space");
        await Assert.That(errors[0].Message).Contains("overflow by");
    }

    private static LinkerSection CreateSection(string name, SectionType type,
        int size, int? fixedAddress = null, int? bank = null)
    {
        var data = new SectionData(name, type, fixedAddress, bank,
            new byte[size], Array.Empty<PatchEntry>());
        return new LinkerSection(data, "test.asm");
    }
}
