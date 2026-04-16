using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class DataBreakpointInfoArguments
{
    [JsonPropertyName("variablesReference")] public int? VariablesReference { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("frameId")] public int? FrameId { get; set; }
}

public sealed class DataBreakpointInfoResponseBody
{
    [JsonPropertyName("dataId")] public string? DataId { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("accessTypes")] public string[] AccessTypes { get; set; } = new[] { "read", "write", "readWrite" };
    [JsonPropertyName("canPersist")] public bool CanPersist { get; set; }
}

public sealed class DataBreakpoint
{
    [JsonPropertyName("dataId")] public string DataId { get; set; } = "";
    [JsonPropertyName("accessType")] public string? AccessType { get; set; }   // "read" | "write" | "readWrite"
    [JsonPropertyName("condition")] public string? Condition { get; set; }
    [JsonPropertyName("hitCondition")] public string? HitCondition { get; set; }
}

public sealed class SetDataBreakpointsArguments
{
    [JsonPropertyName("breakpoints")] public DataBreakpoint[] Breakpoints { get; set; } = Array.Empty<DataBreakpoint>();
}

public sealed class SetDataBreakpointsResponseBody
{
    [JsonPropertyName("breakpoints")] public Breakpoint[] Breakpoints { get; set; } = Array.Empty<Breakpoint>();
}
