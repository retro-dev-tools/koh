using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Syntax;

public class SyntaxNode
{
    private readonly GreenNodeBase _green;

    public SyntaxKind Kind => _green.Kind;
    public SyntaxNode? Parent { get; }
    public int Position { get; }
    public TextSpan FullSpan => new(Position, _green.FullWidth);
    public TextSpan Span => new(Position, _green.FullWidth);

    public SyntaxNode(GreenNodeBase green, SyntaxNode? parent, int position)
    {
        _green = green;
        Parent = parent;
        Position = position;
    }

    public IEnumerable<SyntaxNode> ChildNodes()
    {
        int offset = Position;
        for (int i = 0; i < _green.ChildCount; i++)
        {
            var child = _green.GetChild(i)!;
            if (child is not GreenToken)
                yield return new SyntaxNode(child, this, offset);
            offset += child.FullWidth;
        }
    }

    public IEnumerable<SyntaxToken> ChildTokens()
    {
        int offset = Position;
        for (int i = 0; i < _green.ChildCount; i++)
        {
            var child = _green.GetChild(i)!;
            if (child is GreenToken token)
                yield return new SyntaxToken(token, this, offset);
            offset += child.FullWidth;
        }
    }

    public IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens()
    {
        int offset = Position;
        for (int i = 0; i < _green.ChildCount; i++)
        {
            var child = _green.GetChild(i)!;
            if (child is GreenToken greenToken)
                yield return new SyntaxNodeOrToken(new SyntaxToken(greenToken, this, offset));
            else
                yield return new SyntaxNodeOrToken(new SyntaxNode(child, this, offset));
            offset += child.FullWidth;
        }
    }
}
