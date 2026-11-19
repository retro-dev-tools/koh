using System.Text.Json;
using Koh.Common;

namespace Koh.Native;

internal sealed record NativeSource(string Path, string Text);

internal sealed record NativeBinary(string Path, int[] Data);

internal sealed record NativeResolution(string IncludingFile, string Requested, string Resolved);

internal sealed record NativeSearchRoots(string File, IReadOnlyList<string> Roots);

internal sealed class NativeResponse
{
    public int Version { get; set; }
    public bool Ok { get; set; }
    public NativeDocument[] Documents { get; set; } = [];
    public NativeDiagnostic[] Diagnostics { get; set; } = [];
    public int[] Data { get; set; } = [];
    public JsonElement Object { get; set; }
}

internal sealed class NativeDocument
{
    public string Path { get; set; } = "";
    public NativeInclude[] Includes { get; set; } = [];
}

internal sealed record NativeInclude(string Path, int Start, int Length, bool Binary = false);

internal sealed record NativeDiagnostic(
    string Path,
    int Start,
    int Length,
    string Severity,
    string Message
)
{
    internal Diagnostic ToDiagnostic() =>
        new(
            new TextSpan(Start, Length),
            Message,
            Enum.Parse<DiagnosticSeverity>(Severity),
            string.IsNullOrEmpty(Path) ? null : Path
        );
}
