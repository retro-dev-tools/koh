using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class WriteMemoryArguments
{
    [JsonPropertyName("memoryReference")] public string MemoryReference { get; set; } = "";
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("allowPartial")] public bool AllowPartial { get; set; }
    [JsonPropertyName("data")] public string Data { get; set; } = "";
}

public sealed class WriteMemoryResponseBody
{
    [JsonPropertyName("offset")] public int? Offset { get; set; }
    [JsonPropertyName("bytesWritten")] public int BytesWritten { get; set; }
}
