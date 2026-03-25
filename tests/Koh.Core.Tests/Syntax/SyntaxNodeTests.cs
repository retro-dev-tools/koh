using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Tests.Syntax;

public class SyntaxNodeTests
{
    [Test]
    public async Task SyntaxNode_HasParent()
    {
        var nopGreen = new GreenToken(SyntaxKind.NopKeyword, "nop");
        var stmtGreen = new GreenNode(SyntaxKind.InstructionStatement, [nopGreen]);
        var eofGreen = new GreenToken(SyntaxKind.EndOfFileToken, "");
        var rootGreen = new GreenNode(SyntaxKind.CompilationUnit, [stmtGreen, eofGreen]);

        var root = new SyntaxNode(rootGreen, parent: null, position: 0);

        await Assert.That(root.Parent).IsNull();
        await Assert.That(root.Kind).IsEqualTo(SyntaxKind.CompilationUnit);

        var children = root.ChildNodes().ToList();
        await Assert.That(children).HasCount().EqualTo(1); // only nodes, not tokens
        await Assert.That(children[0].Parent).IsEqualTo(root);
    }

    [Test]
    public async Task SyntaxNode_TracksPosition()
    {
        var trivia = new GreenTrivia(SyntaxKind.WhitespaceTrivia, "  ");
        var nopGreen = new GreenToken(SyntaxKind.NopKeyword, "nop", leadingTrivia: [trivia]);
        var stmtGreen = new GreenNode(SyntaxKind.InstructionStatement, [nopGreen]);
        var eofGreen = new GreenToken(SyntaxKind.EndOfFileToken, "");
        var rootGreen = new GreenNode(SyntaxKind.CompilationUnit, [stmtGreen, eofGreen]);

        var root = new SyntaxNode(rootGreen, parent: null, position: 0);
        var stmt = root.ChildNodes().First();
        var token = stmt.ChildTokens().First();

        await Assert.That(token.Position).IsEqualTo(0);
        await Assert.That(token.Span.Start).IsEqualTo(2);
        await Assert.That(token.Span.Length).IsEqualTo(3);
    }

    [Test]
    public async Task SyntaxNode_Span()
    {
        var nopGreen = new GreenToken(SyntaxKind.NopKeyword, "nop");
        var stmtGreen = new GreenNode(SyntaxKind.InstructionStatement, [nopGreen]);
        var eofGreen = new GreenToken(SyntaxKind.EndOfFileToken, "");
        var rootGreen = new GreenNode(SyntaxKind.CompilationUnit, [stmtGreen, eofGreen]);

        var root = new SyntaxNode(rootGreen, parent: null, position: 0);
        await Assert.That(root.FullSpan).IsEqualTo(new TextSpan(0, 3));
    }
}
