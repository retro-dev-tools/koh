namespace Koh.Core.Syntax;

public readonly struct SyntaxNodeOrToken
{
    public SyntaxNode? AsNode { get; }
    public SyntaxToken? AsToken { get; }
    public bool IsNode => AsNode is not null;
    public bool IsToken => AsToken is not null;
    public SyntaxKind Kind => IsNode ? AsNode!.Kind : AsToken!.Kind;

    public SyntaxNodeOrToken(SyntaxNode node) { AsNode = node; AsToken = null; }
    public SyntaxNodeOrToken(SyntaxToken token) { AsToken = token; AsNode = null; }
}
