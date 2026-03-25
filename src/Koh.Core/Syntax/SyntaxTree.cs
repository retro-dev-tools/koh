using Koh.Core.Diagnostics;
using Koh.Core.Text;

namespace Koh.Core.Syntax;

public sealed class SyntaxTree
{
    public SourceText Text { get; }
    public SyntaxNode Root { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    private SyntaxTree(SourceText text, SyntaxNode root, IReadOnlyList<Diagnostic> diagnostics)
    {
        Text = text;
        Root = root;
        Diagnostics = diagnostics;
    }

    public static SyntaxTree Parse(string text) => Parse(SourceText.From(text));

    public static SyntaxTree Parse(SourceText text)
    {
        var parser = new Parser(text);
        return parser.Parse();
    }

    internal static SyntaxTree Create(SourceText text, SyntaxNode root, IReadOnlyList<Diagnostic> diagnostics)
        => new(text, root, diagnostics);
}
