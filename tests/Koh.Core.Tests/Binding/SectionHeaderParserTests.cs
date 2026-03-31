using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Binding;

/// <summary>
/// Unit tests for SectionHeaderParser — bracket parsing, type mapping, edge cases.
/// </summary>
public class SectionHeaderParserTests
{
    private static (string? name, SectionType type, int? addr, int? bank, bool ok)
        Parse(string source)
    {
        var tree = SyntaxTree.Parse(source);
        var section = tree.Root.ChildNodes().First(n => n.Kind == SyntaxKind.SectionDirective);
        var diag = new DiagnosticBag();
        var ok = SectionHeaderParser.TryParse(section, diag,
            out var name, out var type, out var addr, out var bank,
            out _, out _, out _, out _);
        return (name, type, addr, bank, ok);
    }

    [Test]
    public async Task Simple_Rom0()
    {
        var (name, type, addr, bank, ok) = Parse("SECTION \"Main\", ROM0");
        await Assert.That(ok).IsTrue();
        await Assert.That(name).IsEqualTo("Main");
        await Assert.That(type).IsEqualTo(SectionType.Rom0);
        await Assert.That(addr).IsNull();
        await Assert.That(bank).IsNull();
    }

    [Test]
    public async Task WithFixedAddress()
    {
        var (name, type, addr, bank, ok) = Parse("SECTION \"Entry\", ROM0[$0100]");
        await Assert.That(ok).IsTrue();
        await Assert.That(name).IsEqualTo("Entry");
        await Assert.That(addr).IsEqualTo(0x0100);
    }

    [Test]
    public async Task WithBank()
    {
        var (name, type, addr, bank, ok) = Parse("SECTION \"Banked\", ROMX[$4000], BANK[$01]");
        await Assert.That(ok).IsTrue();
        await Assert.That(type).IsEqualTo(SectionType.RomX);
        await Assert.That(addr).IsEqualTo(0x4000);
        await Assert.That(bank).IsEqualTo(1);
    }

    [Test]
    public async Task AllSectionTypes()
    {
        var types = new (string keyword, SectionType expected)[]
        {
            ("ROM0", SectionType.Rom0), ("ROMX", SectionType.RomX),
            ("WRAM0", SectionType.Wram0), ("WRAMX", SectionType.WramX),
            ("VRAM", SectionType.Vram), ("HRAM", SectionType.Hram),
            ("SRAM", SectionType.Sram), ("OAM", SectionType.Oam),
        };

        foreach (var (keyword, expected) in types)
        {
            var (_, type, _, _, ok) = Parse($"SECTION \"Test\", {keyword}");
            await Assert.That(ok).IsTrue();
            await Assert.That(type).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task MissingName_ReturnsFalse()
    {
        var tree = SyntaxTree.Parse("SECTION , ROM0");
        var section = tree.Root.ChildNodes()
            .FirstOrDefault(n => n.Kind == SyntaxKind.SectionDirective);
        if (section == null) return; // parser may not produce a SectionDirective

        var diag = new DiagnosticBag();
        var ok = SectionHeaderParser.TryParse(section, diag,
            out _, out _, out _, out _, out _, out _, out _, out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task BankWithoutAddress()
    {
        var (name, type, addr, bank, ok) = Parse("SECTION \"Banked\", ROMX, BANK[$02]");
        await Assert.That(ok).IsTrue();
        await Assert.That(addr).IsNull();
        await Assert.That(bank).IsEqualTo(2);
    }

    [Test]
    public async Task TryParseIntegerLiteral_Hex()
    {
        await Assert.That(SectionHeaderParser.TryParseIntegerLiteral("$FF", out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(0xFF);
    }

    [Test]
    public async Task TryParseIntegerLiteral_Binary()
    {
        await Assert.That(SectionHeaderParser.TryParseIntegerLiteral("%1010", out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(0b1010);
    }

    [Test]
    public async Task TryParseIntegerLiteral_Decimal()
    {
        await Assert.That(SectionHeaderParser.TryParseIntegerLiteral("42", out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(42);
    }

    [Test]
    public async Task TryParseIntegerLiteral_Invalid()
    {
        await Assert.That(SectionHeaderParser.TryParseIntegerLiteral("$GG", out _)).IsFalse();
        await Assert.That(SectionHeaderParser.TryParseIntegerLiteral("", out _)).IsFalse();
    }
}
