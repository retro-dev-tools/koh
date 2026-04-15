using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class SetInstructionBreakpointsArguments
{
    [JsonPropertyName("breakpoints")] public InstructionBreakpoint[]? Breakpoints { get; set; }
}

public sealed class InstructionBreakpoint
{
    [JsonPropertyName("instructionReference")] public string InstructionReference { get; set; } = "";
    [JsonPropertyName("offset")] public int Offset { get; set; }
}

public sealed class SetInstructionBreakpointsResponseBody
{
    [JsonPropertyName("breakpoints")] public Breakpoint[] Breakpoints { get; set; } = Array.Empty<Breakpoint>();
}

public sealed class SetFunctionBreakpointsArguments
{
    [JsonPropertyName("breakpoints")] public FunctionBreakpoint[]? Breakpoints { get; set; }
}

public sealed class FunctionBreakpoint
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class BreakpointLocationsArguments
{
    [JsonPropertyName("source")] public Source? Source { get; set; }
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("endLine")] public int? EndLine { get; set; }
}

public sealed class BreakpointLocationsResponseBody
{
    [JsonPropertyName("breakpoints")] public BreakpointLocation[] Breakpoints { get; set; } = Array.Empty<BreakpointLocation>();
}

public sealed class BreakpointLocation
{
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("column")] public int? Column { get; set; }
}
