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
    public NativeSymbol[] Symbols { get; set; } = [];
    public NativeDiagnostic[] Diagnostics { get; set; } = [];
    public int[] Data { get; set; } = [];
    public JsonElement Object { get; set; }
}

internal sealed class NativeDocument
{
    public string Path { get; set; } = "";
    public NativeToken[] Tokens { get; set; } = [];
    public NativeNode[] Nodes { get; set; } = [];
    public NativeScope[] Scopes { get; set; } = [];
    public NativeInclude[] Includes { get; set; } = [];
}

internal sealed class NativeToken
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public int Start { get; set; }
    public int Length { get; set; }
    public int Parent { get; set; }
}

internal sealed class NativeNode
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";
    public int Start { get; set; }
    public int Length { get; set; }
    public NativeChild[] Children { get; set; } = [];
}

internal sealed class NativeChild
{
    public int? Token { get; set; }
    public int? Node { get; set; }
}

internal sealed record NativeScope(int Start, int End, string Name);

internal sealed record NativeInclude(string Path, int Start, int Length, bool Binary = false);

internal sealed record NativeLocation(string Path, int Start, int Length);

internal sealed class NativeSymbol
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Visibility { get; set; } = "";
    public int? Value { get; set; }
    public string? Section { get; set; }
    public string Owner { get; set; } = "";
    public NativeLocation? Definition { get; set; }
    public NativeLocation[] References { get; set; } = [];
    public int MacroArity { get; set; }
}

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
