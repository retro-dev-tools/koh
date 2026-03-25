using Koh.Core.Syntax;

namespace Koh.Core.Diagnostics;

public enum DiagnosticSeverity { Error, Warning, Info }

public sealed class Diagnostic
{
    public TextSpan Span { get; }
    public string Message { get; }
    public DiagnosticSeverity Severity { get; }

    public Diagnostic(TextSpan span, string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        Span = span;
        Message = message;
        Severity = severity;
    }

    public override string ToString() => $"{Severity}: {Message}";
}
