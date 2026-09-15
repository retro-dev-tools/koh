using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class DataDirectiveTests
{
    private static SyntaxNode ParseFirstStatement(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return tree.Root.ChildNodes().First();
    }

    [Test]
    public async Task Db_SingleByte()
    {
        var stmt = ParseFirstStatement("db $00");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.DataDirective);
        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.DbKeyword);
        // One expression child
        var exprs = stmt.ChildNodes().ToList();
        await Assert.That(exprs).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Db_MultipleBytes()
    {
        var stmt = ParseFirstStatement("db $00, $01, $02");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.DataDirective);
        var exprs = stmt.ChildNodes().ToList();
        await Assert.That(exprs).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Db_String()
    {
        var stmt = ParseFirstStatement("db \"Hello\", 0");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.DataDirective);
        var exprs = stmt.ChildNodes().ToList();
        await Assert.That(exprs).Count().IsEqualTo(2);
        await Assert.That(exprs[0].Kind).IsEqualTo(SyntaxKind.LiteralExpression);
    }

    [Test]
    public async Task Dw_Words()
    {
        var stmt = ParseFirstStatement("dw $1234, MyLabel");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.DataDirective);
        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.DwKeyword);
        var exprs = stmt.ChildNodes().ToList();
        await Assert.That(exprs).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Ds_ReserveSpace()
    {
        var stmt = ParseFirstStatement("ds 10");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.DataDirective);
        var exprs = stmt.ChildNodes().ToList();
        await Assert.That(exprs).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Ds_WithFillByte()
    {
        var stmt = ParseFirstStatement("ds 10, $FF");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.DataDirective);
        var exprs = stmt.ChildNodes().ToList();
        await Assert.That(exprs).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Db_ExpressionValues()
    {
        var stmt = ParseFirstStatement("db $FF & $0F, 1 + 2");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.DataDirective);
        var exprs = stmt.ChildNodes().ToList();
        await Assert.That(exprs).Count().IsEqualTo(2);
        await Assert.That(exprs[0].Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        await Assert.That(exprs[1].Kind).IsEqualTo(SyntaxKind.BinaryExpression);
    }

    [Test]
    public async Task DataDirective_NoDiagnostics()
    {
        var tree = SyntaxTree.Parse("db $00, $01, $02");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task DataDirective_FollowedByCode()
    {
        var tree = SyntaxTree.Parse("db $00\nnop");
        var stmts = tree.Root.ChildNodes().ToList();
        await Assert.That(stmts).Count().IsEqualTo(2);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.DataDirective);
        await Assert.That(stmts[1].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task Db_NoOperands()
    {
        var tree = SyntaxTree.Parse("db");
        var stmt = tree.Root.ChildNodes().First();
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.DataDirective);
        var exprs = stmt.ChildNodes().ToList();
        await Assert.That(exprs).IsEmpty();
    }

    [Test]
    public async Task Db_TrailingComma_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("db $01,");
        await Assert.That(tree.Diagnostics).IsNotEmpty();
        var stmt = tree.Root.ChildNodes().First();
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.DataDirective);
    }

    [Test]
    public async Task LabelBeforeData()
    {
        var tree = SyntaxTree.Parse("my_data:\ndb $01, $02");
        var stmts = tree.Root.ChildNodes().ToList();
        await Assert.That(stmts).Count().IsEqualTo(2);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);
        await Assert.That(stmts[1].Kind).IsEqualTo(SyntaxKind.DataDirective);
    }
}
