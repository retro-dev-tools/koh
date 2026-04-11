using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class Source
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
}

public sealed class SourceBreakpoint
{
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("column")] public int? Column { get; set; }
    [JsonPropertyName("condition")] public string? Condition { get; set; }
    [JsonPropertyName("hitCondition")] public string? HitCondition { get; set; }
}

public sealed class SetBreakpointsArguments
{
    [JsonPropertyName("source")] public Source Source { get; set; } = new();
    [JsonPropertyName("breakpoints")] public SourceBreakpoint[]? Breakpoints { get; set; }
    [JsonPropertyName("sourceModified")] public bool SourceModified { get; set; }
}

public sealed class Breakpoint
{
    [JsonPropertyName("id")] public int? Id { get; set; }
    [JsonPropertyName("verified")] public bool Verified { get; set; }
    [JsonPropertyName("line")] public int? Line { get; set; }
    [JsonPropertyName("source")] public Source? Source { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class SetBreakpointsResponseBody
{
    [JsonPropertyName("breakpoints")] public Breakpoint[] Breakpoints { get; set; } = [];
}
