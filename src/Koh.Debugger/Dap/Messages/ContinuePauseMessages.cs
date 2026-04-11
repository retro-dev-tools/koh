using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class ContinueArguments
{
    [JsonPropertyName("threadId")] public int ThreadId { get; set; }
}

public sealed class ContinueResponseBody
{
    [JsonPropertyName("allThreadsContinued")] public bool AllThreadsContinued { get; set; } = true;
}

public sealed class PauseArguments
{
    [JsonPropertyName("threadId")] public int ThreadId { get; set; }
}

public sealed class ConfigurationDoneArguments { }
public sealed class TerminateArguments { }
