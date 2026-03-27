using Koh.Core.Diagnostics;
using Koh.Core.Syntax;
using Koh.Core.Text;
using LspPosition = Microsoft.VisualStudio.LanguageServer.Protocol.Position;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Koh.Lsp;

internal static class PositionUtilities
{
    public static int ToOffset(SourceText source, LspPosition position)
    {
        if (position.Line < 0 || position.Line >= source.Lines.Count)
            return 0;
        var line = source.Lines[position.Line];
        return line.Start + Math.Min(position.Character, line.Length);
    }

    public static LspPosition ToLspPosition(SourceText source, int offset)
    {
        var lineIdx = source.GetLineIndex(offset);
        var col = offset - source.Lines[lineIdx].Start;
        return new LspPosition(lineIdx, col);
    }

    public static LspRange ToLspRange(SourceText source, TextSpan span)
    {
        return new LspRange
        {
            Start = ToLspPosition(source, span.Start),
            End = ToLspPosition(source, span.Start + span.Length),
        };
    }

    public static Microsoft.VisualStudio.LanguageServer.Protocol.Diagnostic ToLspDiagnostic(
        Diagnostic diag, SourceText source)
    {
        var range = diag.Span == default
            ? new LspRange { Start = new LspPosition(0, 0), End = new LspPosition(0, 0) }
            : ToLspRange(source, diag.Span);

        return new Microsoft.VisualStudio.LanguageServer.Protocol.Diagnostic
        {
            Range = range,
            Severity = diag.Severity switch
            {
                DiagnosticSeverity.Error => Microsoft.VisualStudio.LanguageServer.Protocol.DiagnosticSeverity.Error,
                DiagnosticSeverity.Warning => Microsoft.VisualStudio.LanguageServer.Protocol.DiagnosticSeverity.Warning,
                _ => Microsoft.VisualStudio.LanguageServer.Protocol.DiagnosticSeverity.Information,
            },
            Source = "koh",
            Message = diag.Message,
        };
    }
}
