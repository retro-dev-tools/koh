using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class SyntaxKindTests
{
    [Test]
    public async Task SyntaxKind_HasTokenKinds()
    {
        var kind = SyntaxKind.EndOfFileToken;
        await Assert.That(kind).IsEqualTo(SyntaxKind.EndOfFileToken);
    }

    [Test]
    public async Task SyntaxKind_HasTriviaKinds()
    {
        var kind = SyntaxKind.WhitespaceTrivia;
        await Assert.That(kind).IsEqualTo(SyntaxKind.WhitespaceTrivia);
    }

    [Test]
    public async Task SyntaxKind_HasNodeKinds()
    {
        var kind = SyntaxKind.CompilationUnit;
        await Assert.That(kind).IsEqualTo(SyntaxKind.CompilationUnit);
    }
}
