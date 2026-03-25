namespace Koh.Core.Syntax.InternalSyntax;

public sealed class GreenTrivia
{
    public SyntaxKind Kind { get; }
    public string Text { get; }
    public int Width => Text.Length;

    public GreenTrivia(SyntaxKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }
}
