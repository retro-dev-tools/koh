using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Tests.Syntax.InternalSyntax;

public class GreenNodeTests
{
    [Test]
    public async Task GreenToken_StoresKindAndWidth()
    {
        var token = new GreenToken(SyntaxKind.NopKeyword, "nop");
        await Assert.That(token.Kind).IsEqualTo(SyntaxKind.NopKeyword);
        await Assert.That(token.FullWidth).IsEqualTo(3);
    }

    [Test]
    public async Task GreenToken_WithLeadingTrivia()
    {
        var trivia = new GreenTrivia(SyntaxKind.WhitespaceTrivia, "  ");
        var token = new GreenToken(SyntaxKind.NopKeyword, "nop",
            leadingTrivia: [trivia], trailingTrivia: []);
        await Assert.That(token.FullWidth).IsEqualTo(5); // 2 spaces + 3 chars
        await Assert.That(token.Width).IsEqualTo(3); // text only
    }

    [Test]
    public async Task GreenNode_WithChildren()
    {
        var nop = new GreenToken(SyntaxKind.NopKeyword, "nop");
        var newline = new GreenToken(SyntaxKind.EndOfFileToken, "");
        var statement = new GreenNode(SyntaxKind.InstructionStatement, [nop]);
        var unit = new GreenNode(SyntaxKind.CompilationUnit, [statement, newline]);

        await Assert.That(unit.Kind).IsEqualTo(SyntaxKind.CompilationUnit);
        await Assert.That(unit.ChildCount).IsEqualTo(2);
        await Assert.That(unit.FullWidth).IsEqualTo(3);
    }
}
