using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class ProtocolMessage
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

public sealed class Request
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "request";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("arguments")] public System.Text.Json.JsonElement? Arguments { get; set; }
}

public sealed class Response
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "response";
    [JsonPropertyName("request_seq")] public int RequestSeq { get; set; }
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("body")] public object? Body { get; set; }
}

public sealed class Event
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "event";
    [JsonPropertyName("event")] public string EventName { get; set; } = "";
    [JsonPropertyName("body")] public object? Body { get; set; }
}

public sealed class Capabilities
{
    [JsonPropertyName("supportsConfigurationDoneRequest")] public bool SupportsConfigurationDoneRequest { get; set; }
    [JsonPropertyName("supportsFunctionBreakpoints")] public bool SupportsFunctionBreakpoints { get; set; }
    [JsonPropertyName("supportsConditionalBreakpoints")] public bool SupportsConditionalBreakpoints { get; set; }
    [JsonPropertyName("supportsHitConditionalBreakpoints")] public bool SupportsHitConditionalBreakpoints { get; set; }
    [JsonPropertyName("supportsStepBack")] public bool SupportsStepBack { get; set; }
    [JsonPropertyName("supportsSetVariable")] public bool SupportsSetVariable { get; set; }
    [JsonPropertyName("supportsReadMemoryRequest")] public bool SupportsReadMemoryRequest { get; set; }
    [JsonPropertyName("supportsWriteMemoryRequest")] public bool SupportsWriteMemoryRequest { get; set; }
    [JsonPropertyName("supportsDisassembleRequest")] public bool SupportsDisassembleRequest { get; set; }
    [JsonPropertyName("supportsSteppingGranularity")] public bool SupportsSteppingGranularity { get; set; }
    [JsonPropertyName("supportsInstructionBreakpoints")] public bool SupportsInstructionBreakpoints { get; set; }
    [JsonPropertyName("supportsExceptionInfoRequest")] public bool SupportsExceptionInfoRequest { get; set; }
    [JsonPropertyName("supportsBreakpointLocationsRequest")] public bool SupportsBreakpointLocationsRequest { get; set; }
    [JsonPropertyName("supportsEvaluateForHovers")] public bool SupportsEvaluateForHovers { get; set; }
    [JsonPropertyName("supportsDataBreakpoints")] public bool SupportsDataBreakpoints { get; set; }
}
