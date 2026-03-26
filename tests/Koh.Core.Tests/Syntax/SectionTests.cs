using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class SectionTests
{
    private static SyntaxNode ParseFirstStatement(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return tree.Root.ChildNodes().First();
    }

    [Test]
    public async Task SimpleSection()
    {
        var stmt = ParseFirstStatement("SECTION \"Main\", ROM0");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SectionDirective);

        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.SectionKeyword);
    }

    [Test]
    public async Task SectionWithAddress()
    {
        var stmt = ParseFirstStatement("SECTION \"Bank1\", ROMX[$4000]");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SectionDirective);
    }

    [Test]
    public async Task SectionWithBankAndAddress()
    {
        var stmt = ParseFirstStatement("SECTION \"Bank1\", ROMX[$4000], BANK[$01]");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SectionDirective);
        var tree = SyntaxTree.Parse("SECTION \"Bank1\", ROMX[$4000], BANK[$01]");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task SectionWithAlign()
    {
        var stmt = ParseFirstStatement("SECTION \"RAM\", WRAM0, ALIGN[8]");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SectionDirective);
    }

    [Test]
    public async Task SectionFragment()
    {
        var stmt = ParseFirstStatement("SECTION FRAGMENT \"Frag\", ROMX");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SectionDirective);

        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.SectionKeyword);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.FragmentKeyword);
    }

    [Test]
    public async Task SectionUnion()
    {
        var stmt = ParseFirstStatement("SECTION UNION \"Shared\", WRAM0[$C100]");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SectionDirective);
    }

    [Test]
    public async Task SectionFollowedByCode()
    {
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\nnop");
        var stmts = tree.Root.ChildNodes().ToList();
        await Assert.That(stmts).Count().IsEqualTo(2);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.SectionDirective);
        await Assert.That(stmts[1].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task SectionNoDiagnostics()
    {
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task SectionTypes()
    {
        foreach (var type in new[] { "ROM0", "ROMX", "WRAM0", "WRAMX", "VRAM", "HRAM", "SRAM", "OAM" })
        {
            var tree = SyntaxTree.Parse($"SECTION \"Test\", {type}");
            await Assert.That(tree.Root.ChildNodes().First().Kind).IsEqualTo(SyntaxKind.SectionDirective);
            await Assert.That(tree.Diagnostics).IsEmpty();
        }
    }

    [Test]
    public async Task LabelBeforeSection()
    {
        var tree = SyntaxTree.Parse("start:\nSECTION \"Main\", ROM0");
        var stmts = tree.Root.ChildNodes().ToList();
        await Assert.That(stmts).Count().IsEqualTo(2);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);
        await Assert.That(stmts[1].Kind).IsEqualTo(SyntaxKind.SectionDirective);
        await Assert.That(tree.Diagnostics).IsEmpty();
    }
}
