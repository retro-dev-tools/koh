using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Koh.Lsp.Tests;

/// <summary>
/// Builder helpers for LSP test scenarios.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Create a workspace with one or more documents loaded.
    /// Keys are URIs, values are source text.
    /// </summary>
    public static Workspace CreateWorkspace(params (string Uri, string Text)[] documents)
    {
        var workspace = new Workspace();
        foreach (var (uri, text) in documents)
            workspace.OpenDocument(uri, text);
        return workspace;
    }

    /// <summary>
    /// Create a workspace with a single document.
    /// </summary>
    public static Workspace CreateWorkspace(string text, string uri = "file:///test.asm")
    {
        var workspace = new Workspace();
        workspace.OpenDocument(uri, text);
        return workspace;
    }

    public static TextDocumentPositionParams PositionParams(string uri, int line, int character) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(uri) },
            Position = new Position(line, character),
        };

    public static RenameParams RenameParams(string uri, int line, int character, string newName) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(uri) },
            Position = new Position(line, character),
            NewName = newName,
        };

    public static TextDocumentPositionParams SignatureHelpParams(string uri, int line, int character) =>
        new()
        {
            TextDocument = new TextDocumentIdentifier { Uri = new Uri(uri) },
            Position = new Position(line, character),
        };

    /// <summary>
    /// Find the offset of a marker string within source text, useful for positioning
    /// the cursor at a specific symbol in test source code.
    /// </summary>
    public static int FindOffset(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            throw new ArgumentException($"Marker '{marker}' not found in source text.");
        return index;
    }

    /// <summary>
    /// Convert a 0-based offset to (line, character) for LSP Position.
    /// </summary>
    public static (int Line, int Character) OffsetToLineChar(string source, int offset)
    {
        int line = 0;
        int col = 0;
        for (int i = 0; i < offset && i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                col = 0;
            }
            else
            {
                col++;
            }
        }
        return (line, col);
    }
}
