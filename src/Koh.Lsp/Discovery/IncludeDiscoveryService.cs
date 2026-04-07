namespace Koh.Lsp.Discovery;

/// <summary>
/// Holds the result of scanning a single file for INCLUDE directives.
/// </summary>
/// <param name="FilePath">Normalized absolute path of the scanned file.</param>
/// <param name="IncludedFiles">Resolved absolute paths of all discovered INCLUDE targets.</param>
internal sealed record FileDiscoveryInfo(string FilePath, IReadOnlyList<string> IncludedFiles);

/// <summary>
/// Lightweight scanner that extracts RGBDS INCLUDE directives from assembly text.
/// <para>
/// The scanner processes text line-by-line, strips comments, and extracts quoted
/// paths from INCLUDE directives. It does not use a single regex as the only
/// mechanism — instead it combines line-level classification with targeted parsing.
/// </para>
/// </summary>
internal sealed class IncludeDiscoveryService
{
    /// <summary>
    /// Discovers all INCLUDE directives in the given text.
    /// </summary>
    /// <param name="filePath">Absolute path of the file being scanned (used for relative path resolution).</param>
    /// <param name="text">The source text to scan.</param>
    /// <param name="workspaceFolderPath">The workspace root (acts as CWD for RGBDS resolution).</param>
    /// <returns>A <see cref="FileDiscoveryInfo"/> with the file path and resolved include targets.</returns>
    public FileDiscoveryInfo Discover(string filePath, string text, string workspaceFolderPath)
    {
        var normalizedFilePath = Path.GetFullPath(filePath);
        var includes = new List<string>();

        var inBlockComment = false;

        foreach (var rawLine in EnumerateLines(text))
        {
            // Handle block comments: strip or skip portions inside /* ... */
            var stripped = ProcessBlockComments(rawLine, ref inBlockComment);
            if (stripped.Length == 0)
                continue;

            var line = stripped.AsSpan().TrimStart();

            // Skip line comments (entire line is a comment)
            if (line.StartsWith(";"))
                continue;

            // Try to extract an INCLUDE directive from this line
            var includePath = TryExtractInclude(line);
            if (includePath is not null)
            {
                var resolved = ResolveIncludePath(normalizedFilePath, includePath, workspaceFolderPath);
                includes.Add(resolved);
            }
        }

        return new FileDiscoveryInfo(normalizedFilePath, includes);
    }

    /// <summary>
    /// Enumerates lines from the text without allocating a full string[] up front.
    /// Handles \r\n, \n, and \r line endings.
    /// </summary>
    private static IEnumerable<string> EnumerateLines(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    /// <summary>
    /// Strips block comment regions from a line, tracking whether we are inside
    /// a /* ... */ block across lines. Returns the remaining content.
    /// </summary>
    private static string ProcessBlockComments(string line, ref bool inBlockComment)
    {
        // Fast path: no block comment markers at all and not inside one
        if (!inBlockComment && !line.Contains("/*", StringComparison.Ordinal))
            return line;

        // Need to process character by character
        var buffer = new char[line.Length];
        var len = 0;

        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                // Look for */
                if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++; // skip the '/'
                }
            }
            else
            {
                // Look for /*
                if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i++; // skip the '*'
                }
                else
                {
                    buffer[len++] = line[i];
                }
            }
        }

        if (len == 0)
            return string.Empty;

        return new string(buffer, 0, len);
    }

    /// <summary>
    /// Attempts to extract the include path from a line that may contain an INCLUDE directive.
    /// Handles optional label prefix (e.g. "main: INCLUDE ...") and inline comments.
    /// </summary>
    private static string? TryExtractInclude(ReadOnlySpan<char> line)
    {
        // Find the INCLUDE keyword (case-insensitive).
        // It can appear at the start of the line or after a label (label: INCLUDE ...).
        var searchPos = 0;
        while (searchPos <= line.Length - 7) // "INCLUDE" is 7 chars
        {
            var idx = line[searchPos..].IndexOf("INCLUDE", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return null;

            var absoluteIdx = searchPos + idx;

            // The INCLUDE keyword must be preceded by whitespace, colon, or start of line
            if (absoluteIdx > 0)
            {
                var preceding = line[absoluteIdx - 1];
                if (preceding != ' ' && preceding != '\t' && preceding != ':')
                {
                    searchPos = absoluteIdx + 7;
                    continue;
                }
            }

            // The INCLUDE keyword must be followed by whitespace
            var afterIdx = absoluteIdx + 7;
            if (afterIdx >= line.Length || (line[afterIdx] != ' ' && line[afterIdx] != '\t'))
            {
                searchPos = absoluteIdx + 7;
                continue;
            }

            // Found a valid INCLUDE keyword — now extract the quoted path
            var remainder = line[(afterIdx + 1)..].TrimStart();
            return ExtractQuotedPath(remainder);
        }

        return null;
    }

    /// <summary>
    /// Extracts a double-quoted path from the beginning of the span.
    /// Respects inline comments (;) that appear outside the quoted string.
    /// </summary>
    private static string? ExtractQuotedPath(ReadOnlySpan<char> span)
    {
        // Must start with a double quote
        if (span.IsEmpty || span[0] != '"')
            return null;

        // Find the closing quote
        var closingQuoteIdx = span[1..].IndexOf('"');
        if (closingQuoteIdx < 0)
            return null; // Malformed — no closing quote; tolerate gracefully

        var path = span.Slice(1, closingQuoteIdx);
        if (path.IsEmpty)
            return null;

        return path.ToString();
    }

    /// <summary>
    /// Resolves an include path following RGBDS conventions:
    /// 1. Try relative to the workspace folder (CWD) first
    /// 2. Then relative to the containing file's directory
    /// Normalizes the result with <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    private static string ResolveIncludePath(string containingFile, string includePath, string workspaceFolderPath)
    {
        // Try relative to workspace folder (CWD) first
        var cwdPath = Path.GetFullPath(Path.Combine(workspaceFolderPath, includePath));
        if (File.Exists(cwdPath))
            return cwdPath;

        // Then relative to the containing file's directory
        var dir = Path.GetDirectoryName(containingFile) ?? workspaceFolderPath;
        return Path.GetFullPath(Path.Combine(dir, includePath));
    }
}
