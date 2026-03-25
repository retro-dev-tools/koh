using System.Collections;
using Koh.Core.Syntax;

namespace Koh.Core.Diagnostics;

public sealed class DiagnosticBag : IEnumerable<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = [];

    public void Report(TextSpan span, string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        _diagnostics.Add(new Diagnostic(span, message, severity));
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
