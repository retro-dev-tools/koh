using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class LaunchRequestArguments
{
    [JsonPropertyName("program")] public string Program { get; set; } = "";
    [JsonPropertyName("debugInfo")] public string? DebugInfo { get; set; }
    [JsonPropertyName("hardwareMode")] public string HardwareMode { get; set; } = "auto";
    [JsonPropertyName("stopOnEntry")] public bool StopOnEntry { get; set; }
}
