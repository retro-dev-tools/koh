using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class SymbolDirectiveTests
{
    private static SyntaxNode ParseFirstStatement(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return tree.Root.ChildNodes().First();
    }

    [Test]
    public async Task Equ()
    {
        var stmt = ParseFirstStatement("MY_CONST EQU $10");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SymbolDirective);
        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.IdentifierToken);
        await Assert.That(tokens[0].Text).IsEqualTo("MY_CONST");
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.EquKeyword);
    }

    [Test]
    public async Task Equs()
    {
        var stmt = ParseFirstStatement("MY_STR EQUS \"hello\"");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SymbolDirective);
        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.IdentifierToken);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.EqusKeyword);
    }

    [Test]
    public async Task RedefEqu()
    {
        var stmt = ParseFirstStatement("REDEF MY_CONST EQU $20");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SymbolDirective);
        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.RedefKeyword);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.IdentifierToken);
        await Assert.That(tokens[2].Kind).IsEqualTo(SyntaxKind.EquKeyword);
    }

    [Test]
    public async Task DefEquals()
    {
        // DEF MY_VAR = 5 — DEF as directive (not function call)
        var stmt = ParseFirstStatement("DEF MY_VAR = 5");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SymbolDirective);
        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.DefKeyword);
    }

    [Test]
    public async Task DefFunction_NotDirective()
    {
        // DEF(symbol) should be a function call inside an instruction, not a directive
        var tree = SyntaxTree.Parse("ld a, DEF(MySymbol)");
        var stmts = tree.Root.ChildNodes().ToList();
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Export()
    {
        var stmt = ParseFirstStatement("EXPORT my_label");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SymbolDirective);
        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.ExportKeyword);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.IdentifierToken);
    }

    [Test]
    public async Task Purge()
    {
        var stmt = ParseFirstStatement("PURGE MY_CONST");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SymbolDirective);
        var tokens = stmt.ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.PurgeKeyword);
    }

    [Test]
    public async Task Equ_NoDiagnostics()
    {
        var tree = SyntaxTree.Parse("MY_CONST EQU $10");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task EquFollowedByCode()
    {
        var tree = SyntaxTree.Parse("MY_CONST EQU $10\nnop");
        var stmts = tree.Root.ChildNodes().ToList();
        await Assert.That(stmts).Count().IsEqualTo(2);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.SymbolDirective);
        await Assert.That(stmts[1].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task DefEquals_NoDiagnostics()
    {
        var tree = SyntaxTree.Parse("DEF MY_VAR = 5");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task ExportMultiple()
    {
        var tree = SyntaxTree.Parse("EXPORT label1, label2, label3");
        var stmt = tree.Root.ChildNodes().First();
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SymbolDirective);
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task PurgeMultiple()
    {
        var tree = SyntaxTree.Parse("PURGE sym1, sym2");
        var stmt = tree.Root.ChildNodes().First();
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SymbolDirective);
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task EquWithExpression()
    {
        // EQU value should be parsed as an expression, not flat tokens
        var stmt = ParseFirstStatement("MY_CONST EQU $FF & $0F");
        await Assert.That(stmt.Kind).IsEqualTo(SyntaxKind.SymbolDirective);
        var exprs = stmt.ChildNodes().ToList();
        await Assert.That(exprs.Any(n => n.Kind == SyntaxKind.BinaryExpression)).IsTrue();
    }
}
