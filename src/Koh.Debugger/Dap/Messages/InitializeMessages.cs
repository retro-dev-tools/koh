using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class InitializeRequestArguments
{
    [JsonPropertyName("clientID")] public string? ClientId { get; set; }
    [JsonPropertyName("clientName")] public string? ClientName { get; set; }
    [JsonPropertyName("adapterID")] public string? AdapterId { get; set; }
    [JsonPropertyName("linesStartAt1")] public bool LinesStartAt1 { get; set; } = true;
    [JsonPropertyName("columnsStartAt1")] public bool ColumnsStartAt1 { get; set; } = true;
    [JsonPropertyName("pathFormat")] public string? PathFormat { get; set; }
}
