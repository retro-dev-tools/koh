namespace Koh.Core.Syntax.InternalSyntax;

public sealed class GreenToken : GreenNodeBase
{
    public string Text { get; }
    public IReadOnlyList<GreenTrivia> LeadingTrivia { get; }
    public IReadOnlyList<GreenTrivia> TrailingTrivia { get; }

    public override int Width => Text.Length;
    public override int FullWidth => LeadingTriviaWidth + Width + TrailingTriviaWidth;
    public override int ChildCount => 0;

    public int LeadingTriviaWidth => LeadingTrivia.Sum(t => t.Width);
    public int TrailingTriviaWidth => TrailingTrivia.Sum(t => t.Width);

    public GreenToken(SyntaxKind kind, string text,
        IReadOnlyList<GreenTrivia>? leadingTrivia = null,
        IReadOnlyList<GreenTrivia>? trailingTrivia = null)
        : base(kind)
    {
        Text = text;
        LeadingTrivia = leadingTrivia ?? [];
        TrailingTrivia = trailingTrivia ?? [];
    }

    public override GreenNodeBase? GetChild(int index) => null;
}
