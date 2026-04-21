using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

/// <summary>
/// Body of the DAP "stopped" event — sent by the adapter when the
/// target pauses (entry, breakpoint, pause request, step complete).
/// VS Code reacts by switching the debug UI to paused state and
/// asking for stackTrace / scopes / variables.
/// </summary>
public sealed class StoppedEventBody
{
    [JsonPropertyName("reason")]            public string  Reason            { get; set; } = "";
    [JsonPropertyName("threadId")]          public int     ThreadId          { get; set; }
    [JsonPropertyName("allThreadsStopped")] public bool    AllThreadsStopped { get; set; }
    [JsonPropertyName("description")]       public string? Description       { get; set; }
    [JsonPropertyName("text")]              public string? Text              { get; set; }
}
