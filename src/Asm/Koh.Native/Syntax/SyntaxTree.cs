using Koh.Common;

namespace Koh.Native.Syntax;

public sealed class SyntaxTree
{
    public SourceText Text { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    internal NativeDocument Document { get; }

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
}
