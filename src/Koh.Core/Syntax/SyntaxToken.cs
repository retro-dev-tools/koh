using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Syntax;

public sealed class SyntaxToken
{
    private readonly GreenToken _green;

    public SyntaxKind Kind => _green.Kind;
    public string Text => _green.Text;
    public int Position { get; }
    public SyntaxNode? Parent { get; }

    public TextSpan Span => new(Position + _green.LeadingTriviaWidth, _green.Width);
    public TextSpan FullSpan => new(Position, _green.FullWidth);

    public IEnumerable<SyntaxTrivia> LeadingTrivia
    {
        get
        {
            int pos = Position;
            foreach (var trivia in _green.LeadingTrivia)
            {
                yield return new SyntaxTrivia(trivia, pos);
                pos += trivia.Width;
            }
        }
    }

    public IEnumerable<SyntaxTrivia> TrailingTrivia
    {
        get
        {
            int pos = Position + _green.LeadingTriviaWidth + _green.Width;
            foreach (var trivia in _green.TrailingTrivia)
            {
                yield return new SyntaxTrivia(trivia, pos);
                pos += trivia.Width;
            }
        }
    }

    public bool IsMissing => _green.Width == 0 && Kind != SyntaxKind.EndOfFileToken;

    internal SyntaxToken(GreenToken green, SyntaxNode? parent, int position)
    {
        _green = green;
        Parent = parent;
        Position = position;
    }
}
