using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class EvaluateArguments
{
    [JsonPropertyName("expression")] public string Expression { get; set; } = "";
    [JsonPropertyName("frameId")] public int? FrameId { get; set; }
    [JsonPropertyName("context")] public string? Context { get; set; }
}

public sealed class EvaluateResponseBody
{
    [JsonPropertyName("result")] public string Result { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("variablesReference")] public int VariablesReference { get; set; }
    [JsonPropertyName("memoryReference")] public string? MemoryReference { get; set; }
}
