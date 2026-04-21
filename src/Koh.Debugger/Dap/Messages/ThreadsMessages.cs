using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

// "DapThread" rather than plain Thread to sidestep the clash with
// System.Threading.Thread — the DAP wire format still serialises the
// JSON key as "threads" / "name" / "id", just the C# type has a
// distinguishing prefix.
public sealed class DapThread
{
    [JsonPropertyName("id")]   public int Id   { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class ThreadsResponseBody
{
    [JsonPropertyName("threads")] public DapThread[] Threads { get; set; } = System.Array.Empty<DapThread>();
}
