using Koh.Core.Syntax;

namespace Koh.Core.Diagnostics;

public enum DiagnosticSeverity { Error, Warning, Info }

public sealed class Diagnostic
{
    public TextSpan Span { get; }
    public string Message { get; }
    public DiagnosticSeverity Severity { get; }
    public string? FilePath { get; }

    public Diagnostic(TextSpan span, string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Error,
        string? filePath = null)
    {
        Span = span;
        Message = message;
        Severity = severity;
        FilePath = filePath;
    }

    public override string ToString() => $"{Severity}: {Message}";
}
