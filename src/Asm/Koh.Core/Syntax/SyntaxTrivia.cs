using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Syntax;

public readonly struct SyntaxTrivia
{
    private readonly GreenTrivia _green;

    public SyntaxKind Kind => _green.Kind;
    public string Text => _green.Text;
    public int Position { get; }
    public TextSpan Span => new(Position, _green.Width);

    internal SyntaxTrivia(GreenTrivia green, int position)
    {
        _green = green;
        Position = position;
    }
}
