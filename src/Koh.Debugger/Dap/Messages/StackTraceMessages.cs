using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class StackTraceArguments
{
    [JsonPropertyName("threadId")] public int ThreadId { get; set; }
    [JsonPropertyName("startFrame")] public int StartFrame { get; set; }
    [JsonPropertyName("levels")] public int Levels { get; set; }
}

public sealed class StackTraceResponseBody
{
    [JsonPropertyName("stackFrames")] public StackFrame[] StackFrames { get; set; } = Array.Empty<StackFrame>();
    [JsonPropertyName("totalFrames")] public int TotalFrames { get; set; }
}

public sealed class StackFrame
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("source")] public Source? Source { get; set; }
    [JsonPropertyName("line")] public int Line { get; set; }
    [JsonPropertyName("column")] public int Column { get; set; } = 1;
    [JsonPropertyName("instructionPointerReference")] public string? InstructionPointerReference { get; set; }
}
