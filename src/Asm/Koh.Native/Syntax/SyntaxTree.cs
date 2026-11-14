using Koh.Common;

namespace Koh.Native.Syntax;

public sealed class SyntaxTree
{
    public SourceText Text { get; }
    public SyntaxNode Root { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public IReadOnlyList<string> Includes =>
        Document
            .Includes.Where(include => !include.Binary)
            .Select(include => include.Path)
            .ToArray();
    internal NativeDocument Document { get; }
    internal Dictionary<int, SyntaxNode> Nodes { get; } = [];
    internal Dictionary<int, SyntaxToken> Tokens { get; } = [];
    internal SyntaxToken[] OrderedTokens { get; }

    internal static string SourcePath(SourceText source) =>
        string.IsNullOrEmpty(source.FilePath) ? "<anonymous>" : source.FilePath;

    internal SyntaxTree(
        SourceText text,
        NativeDocument document,
        IEnumerable<NativeDiagnostic> diagnostics
    )
    {
        Text = text;
        Document = document;
        Diagnostics = diagnostics
            .Where(d => d.Path == document.Path)
            .Select(d => d.ToDiagnostic())
            .ToArray();
        foreach (var node in document.Nodes)
            Nodes.Add(node.Id, new SyntaxNode(this, node));
        foreach (var token in document.Tokens)
            Tokens.Add(token.Id, new SyntaxToken(this, token));
        foreach (var node in document.Nodes)
        foreach (var child in node.Children)
            if (child.Node is int id)
                Nodes[id].Parent = Nodes[node.Id];
        Root = Nodes[0];
        OrderedTokens = Tokens.Values.OrderBy(token => token.Position).ToArray();
    }

    public static SyntaxTree Parse(string text) => Parse(SourceText.From(text));

    public static SyntaxTree Parse(SourceText text)
    {
        var path = SourcePath(text);
        var response = NativeClient.Shared.Request(
            "parse",
            [new NativeSource(path, text.ToString())],
            [path]
        );
        var document =
            response.Documents.SingleOrDefault(d => d.Path == path)
            ?? throw new InvalidDataException("Native parser omitted the requested document");
        return new SyntaxTree(text, document, response.Diagnostics);
    }

    public SyntaxTree WithChanges(SourceText text) =>
        text.ToString() == Text.ToString() ? this : Parse(text);
}

public sealed class SyntaxNode
{
    private readonly SyntaxTree tree;
    private readonly NativeNode node;

    internal SyntaxNode(SyntaxTree tree, NativeNode node)
    {
        this.tree = tree;
        this.node = node;
        Kind = Enum.Parse<SyntaxKind>(node.Kind);
    }

    public SyntaxKind Kind { get; }
    public SyntaxNode? Parent { get; internal set; }
    public int Position => node.Start;
    public TextSpan Span => new(node.Start, node.Length);
    public TextSpan FullSpan => Span;

    public IEnumerable<SyntaxNode> ChildNodes() =>
        node.Children.Where(c => c.Node != null).Select(c => tree.Nodes[c.Node!.Value]);

    public IEnumerable<SyntaxToken> ChildTokens() =>
        node
            .Children.Where(c => c.Token != null)
            .Select(c => tree.Tokens[c.Token!.Value])
            .Where(t => !t.IsTrivia);

    public IEnumerable<SyntaxNodeOrToken> ChildNodesAndTokens() =>
        node.Children.Select(c =>
            c.Node is int id
                ? new SyntaxNodeOrToken(tree.Nodes[id])
                : new SyntaxNodeOrToken(tree.Tokens[c.Token!.Value])
        );

    public SyntaxToken? FindToken(int position)
    {
        if (position < Position || position >= Span.End)
            return null;
        return tree.OrderedTokens.FirstOrDefault(t =>
                !t.IsWhitespace && t.Span.Start <= position && position < t.Span.End
            )
            ?? tree.OrderedTokens.FirstOrDefault(t =>
                !t.IsTrivia && t.Position >= position && t.Position < Span.End
            );
    }
}

public sealed class SyntaxToken
{
    private readonly SyntaxTree tree;
    private readonly NativeToken token;

    internal SyntaxToken(SyntaxTree tree, NativeToken token)
    {
        this.tree = tree;
        this.token = token;
        Kind = Enum.Parse<SyntaxKind>(token.Kind);
    }

    public SyntaxKind Kind { get; }
    public string Text => token.Text;
    public int Position => token.Start;
    public TextSpan Span => new(token.Start, token.Length);
    public TextSpan FullSpan => Span;
    public SyntaxNode? Parent => tree.Nodes.GetValueOrDefault(token.Parent);
    public bool IsMissing => token.Length == 0 && Kind != SyntaxKind.EndOfFileToken;
    internal bool IsWhitespace => Kind is SyntaxKind.WhitespaceTrivia or SyntaxKind.NewlineTrivia;
    internal bool IsTrivia =>
        Kind
            is SyntaxKind.WhitespaceTrivia
                or SyntaxKind.NewlineTrivia
                or SyntaxKind.LineCommentTrivia
                or SyntaxKind.BlockCommentTrivia;
    public IEnumerable<SyntaxTrivia> LeadingTrivia => [];
    public IEnumerable<SyntaxTrivia> TrailingTrivia => [];
}

public readonly record struct SyntaxTrivia(SyntaxKind Kind, string Text, int Position)
{
    public TextSpan Span => new(Position, Text.Length);
}

public readonly struct SyntaxNodeOrToken
{
    public SyntaxNode? AsNode { get; }
    public SyntaxToken? AsToken { get; }
    public bool IsNode => AsNode != null;
    public bool IsToken => AsToken != null;

    public SyntaxNodeOrToken(SyntaxNode node)
    {
        AsNode = node;
        AsToken = null;
    }

    public SyntaxNodeOrToken(SyntaxToken token)
    {
        AsNode = null;
        AsToken = token;
    }
}
