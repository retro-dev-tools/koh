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
        await Assert.That(children).Count().IsEqualTo(1); // only nodes, not tokens
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

    [Test]
    public async Task SyntaxNode_Span_ExcludesTrivia()
    {
        // "  nop\n" — leading whitespace (2) + "nop" (3) + trailing newline (1)
        var tree = SyntaxTree.Parse("  nop\n");
        var stmt = tree.Root.ChildNodes().First();

        // FullSpan includes trivia
        await Assert.That(stmt.FullSpan.Start).IsEqualTo(0);

        // Span excludes leading whitespace and trailing newline
        await Assert.That(stmt.Span.Start).IsEqualTo(2);
        await Assert.That(stmt.Span.Length).IsLessThan(stmt.FullSpan.Length);
    }

    // =========================================================================
    // FindToken
    // =========================================================================

    [Test]
    public async Task FindToken_AtStartOfToken_ReturnsToken()
    {
        var tree = SyntaxTree.Parse("nop");
        var token = tree.Root.FindToken(0);

        await Assert.That(token).IsNotNull();
        await Assert.That(token!.Kind).IsEqualTo(SyntaxKind.NopKeyword);
    }

    [Test]
    public async Task FindToken_InsideToken_ReturnsToken()
    {
        var tree = SyntaxTree.Parse("halt");
        var token = tree.Root.FindToken(2);

        await Assert.That(token).IsNotNull();
        await Assert.That(token!.Kind).IsEqualTo(SyntaxKind.HaltKeyword);
    }

    [Test]
    public async Task FindToken_AtBoundaryBetweenTokens()
    {
        var tree = SyntaxTree.Parse("nop\nhalt");
        var tokenAtNop = tree.Root.FindToken(0);
        var tokenAtHalt = tree.Root.FindToken(4);

        await Assert.That(tokenAtNop).IsNotNull();
        await Assert.That(tokenAtNop!.Text).IsEqualTo("nop");

        await Assert.That(tokenAtHalt).IsNotNull();
        await Assert.That(tokenAtHalt!.Text).IsEqualTo("halt");
    }

    [Test]
    public async Task FindToken_PastEnd_ReturnsNull()
    {
        var tree = SyntaxTree.Parse("nop");
        var token = tree.Root.FindToken(100);

        await Assert.That(token).IsNull();
    }

    [Test]
    public async Task FindToken_NegativePosition_ReturnsNull()
    {
        var tree = SyntaxTree.Parse("nop");
        var token = tree.Root.FindToken(-1);

        await Assert.That(token).IsNull();
    }

    [Test]
    public async Task FindToken_RecursesIntoNestedNodes()
    {
        var tree = SyntaxTree.Parse("ld a, $42");
        var token = tree.Root.FindToken(6);

        await Assert.That(token).IsNotNull();
        await Assert.That(token!.Kind).IsEqualTo(SyntaxKind.NumberLiteral);
        await Assert.That(token.Text).IsEqualTo("$42");
    }
}
