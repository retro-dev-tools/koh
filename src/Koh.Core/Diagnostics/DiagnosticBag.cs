using System.Collections;
using Koh.Core.Syntax;

namespace Koh.Core.Diagnostics;

public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    /// <summary>
    /// A no-op diagnostic bag that silently discards all reports.
    /// Used during pre-scan phases where unresolved expressions are expected and
    /// should not produce user-visible diagnostics.
    /// </summary>
    public static readonly DiagnosticBag Null = new(isNull: true);

    private readonly bool _isNull;
    private readonly List<Diagnostic> _diagnostics = [];

    public DiagnosticBag() { }

    private DiagnosticBag(bool isNull) => _isNull = isNull;

    /// <summary>
    /// Ambient file context — set before processing nodes from a given file.
    /// Used as default when Report() is called without an explicit filePath.
    /// </summary>
    private string? _currentFilePath;

    public string? CurrentFilePath
    {
        get => _currentFilePath;
        set { if (!_isNull) _currentFilePath = value; }
    }

    public void Report(TextSpan span, string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error,
        string? filePath = null)
    {
        if (_isNull) return;
        _diagnostics.Add(new Diagnostic(span, message, severity, filePath ?? CurrentFilePath));
    }

    public void ReportUnexpectedToken(TextSpan span, SyntaxKind actual, SyntaxKind expected)
    {
        Report(span, $"Unexpected token '{actual}', expected '{expected}'");
    }

    public void ReportBadCharacter(int position, char character)
    {
        Report(new TextSpan(position, 1), $"Bad character input: '{character}'");
    }

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public IReadOnlyList<Diagnostic> ToList() => _diagnostics;
}
