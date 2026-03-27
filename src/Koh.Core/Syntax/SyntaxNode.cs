using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Syntax;

public class SyntaxNode
{
    private readonly GreenNodeBase _green;

    public SyntaxKind Kind => _green.Kind;
    public SyntaxNode? Parent { get; }
    public int Position { get; }

    /// <summary>
    /// The underlying green node. Used by the binder/evaluator to walk the
    /// immutable tree without red-node overhead.
    /// </summary>
    internal GreenNodeBase Green => _green;
    public TextSpan FullSpan => new(Position, _green.FullWidth);

    // TODO: Span should exclude leading trivia of the first token and trailing trivia of the
    // last token. GreenNode.Width currently returns FullWidth (no trivia stripping for
    // composite nodes), so Span == FullSpan for all SyntaxNodes. Fix requires GreenNode to
    // track inner width separately, which is a green-layer change deferred to a future phase.
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

    /// <summary>
    /// Find the innermost token at the given absolute position.
    /// Returns null if position is outside this node's span.
    /// </summary>
    public SyntaxToken? FindToken(int position)
    {
        if (position < Position || position >= Position + _green.FullWidth)
            return null;

        int offset = Position;
        for (int i = 0; i < _green.ChildCount; i++)
        {
            var child = _green.GetChild(i)!;
            int childEnd = offset + child.FullWidth;
            if (position < childEnd)
            {
                if (child is GreenToken greenToken)
                    return new SyntaxToken(greenToken, this, offset);
                // Recurse into child node
                var childNode = new SyntaxNode(child, this, offset);
                return childNode.FindToken(position);
            }
            offset = childEnd;
        }
        return null;
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
