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

    /// <summary>
    /// Span excluding leading trivia of the first token and trailing trivia of the last token.
    /// </summary>
    public TextSpan Span
    {
        get
        {
            // Walk to find first and last tokens for trivia-free span
            int start = Position;
            int end = Position + _green.FullWidth;

            var firstToken = FindFirstToken(_green);
            if (firstToken != null)
            {
                int firstOffset = GetOffsetToChild(_green, firstToken);
                if (firstOffset >= 0)
                    start = Position + firstOffset + firstToken.LeadingTriviaWidth;
            }

            var lastToken = FindLastToken(_green);
            if (lastToken != null)
            {
                int lastOffset = GetOffsetToChild(_green, lastToken);
                if (lastOffset >= 0)
                    end = Position + lastOffset + lastToken.FullWidth - lastToken.TrailingTriviaWidth;
            }

            return new TextSpan(start, Math.Max(0, end - start));
        }
    }

    private static GreenToken? FindFirstToken(GreenNodeBase node)
    {
        if (node is GreenToken t) return t;
        if (node is not GreenNode gn) return null;
        for (int i = 0; i < gn.ChildCount; i++)
        {
            var child = gn.GetChild(i);
            if (child != null)
            {
                var result = FindFirstToken(child);
                if (result != null) return result;
            }
        }
        return null;
    }

    private static GreenToken? FindLastToken(GreenNodeBase node)
    {
        if (node is GreenToken t) return t;
        if (node is not GreenNode gn) return null;
        for (int i = gn.ChildCount - 1; i >= 0; i--)
        {
            var child = gn.GetChild(i);
            if (child != null)
            {
                var result = FindLastToken(child);
                if (result != null) return result;
            }
        }
        return null;
    }

    private static int GetOffsetToChild(GreenNodeBase parent, GreenToken target)
    {
        if (parent is GreenToken t) return ReferenceEquals(t, target) ? 0 : -1;
        if (parent is not GreenNode gn) return -1;
        int offset = 0;
        for (int i = 0; i < gn.ChildCount; i++)
        {
            var child = gn.GetChild(i);
            if (child == null) continue;
            if (ReferenceEquals(child, target)) return offset;
            var inner = GetOffsetToChild(child, target);
            if (inner >= 0) return offset + inner;
            offset += child.FullWidth;
        }
        return -1;
    }

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
