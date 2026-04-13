using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class ReadMemoryArguments
{
    [JsonPropertyName("memoryReference")] public string MemoryReference { get; set; } = "";
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
}

public sealed class ReadMemoryResponseBody
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("unreadableBytes")] public int UnreadableBytes { get; set; }
    [JsonPropertyName("data")] public string Data { get; set; } = "";
}
