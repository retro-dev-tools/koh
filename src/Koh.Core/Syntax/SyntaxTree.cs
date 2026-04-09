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

    /// <summary>
    /// Computes the minimal single <see cref="TextChange"/> that transforms
    /// <paramref name="oldText"/> into <paramref name="newText"/>.
    /// Returns <c>null</c> when the texts are identical.
    /// The algorithm is deterministic: it finds the longest common prefix and
    /// longest common suffix (that does not overlap the prefix), yielding a
    /// unique minimal change span.
    /// </summary>
    public static TextChange? ComputeChange(SourceText oldText, SourceText newText)
    {
        string oldStr = oldText.ToString();
        string newStr = newText.ToString();

        int prefixLen = 0;
        int minLen = Math.Min(oldStr.Length, newStr.Length);
        while (prefixLen < minLen && oldStr[prefixLen] == newStr[prefixLen])
            prefixLen++;

        int suffixLen = 0;
        int maxSuffix = minLen - prefixLen;
        while (suffixLen < maxSuffix &&
               oldStr[oldStr.Length - 1 - suffixLen] == newStr[newStr.Length - 1 - suffixLen])
            suffixLen++;

        int oldChangeLen = oldStr.Length - prefixLen - suffixLen;
        int newChangeLen = newStr.Length - prefixLen - suffixLen;

        if (oldChangeLen == 0 && newChangeLen == 0)
            return null;

        return new TextChange(
            new TextSpan(prefixLen, oldChangeLen),
            newStr.Substring(prefixLen, newChangeLen));
    }

    /// <summary>
    /// Returns a new <see cref="SyntaxTree"/> for the given <paramref name="newText"/>,
    /// attempting incremental reparse when possible and falling back to a full
    /// parse otherwise.
    /// </summary>
    public SyntaxTree WithChanges(SourceText newText)
    {
        var change = ComputeChange(Text, newText);
        if (change is null)
            return this; // texts are identical

        var incremental = IncrementalParser.TryReparse(this, change.Value, newText);
        if (incremental is not null)
            return incremental;

        // Fallback: full parse
        return Parse(newText);
    }

    internal static SyntaxTree Create(SourceText text, SyntaxNode root, IReadOnlyList<Diagnostic> diagnostics)
        => new(text, root, diagnostics);
}
