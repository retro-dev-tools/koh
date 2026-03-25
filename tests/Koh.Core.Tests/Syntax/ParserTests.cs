using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class ParserTests
{
    [Test]
    public async Task Parser_Nop()
    {
        var tree = SyntaxTree.Parse("nop");
        var root = tree.Root;

        await Assert.That(root.Kind).IsEqualTo(SyntaxKind.CompilationUnit);
        var statements = root.ChildNodes().ToList();
        await Assert.That(statements).HasCount().EqualTo(1);
        await Assert.That(statements[0].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task Parser_LdAB()
    {
        var tree = SyntaxTree.Parse("ld a, b");
        var root = tree.Root;
        var stmt = root.ChildNodes().First();

        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.InstructionStatement);
        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.LdKeyword);
    }

    [Test]
    public async Task Parser_NoDiagnostics_ForValidInput()
    {
        var tree = SyntaxTree.Parse("nop");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Parser_ProducesTree_ForInvalidInput()
    {
        var tree = SyntaxTree.Parse("??? invalid");
        await Assert.That(tree.Root).IsNotNull();
        await Assert.That(tree.Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task Parser_MultipleStatements()
    {
        var tree = SyntaxTree.Parse("nop\nnop");
        var statements = tree.Root.ChildNodes().ToList();
        await Assert.That(statements).HasCount().EqualTo(2);
    }
}
