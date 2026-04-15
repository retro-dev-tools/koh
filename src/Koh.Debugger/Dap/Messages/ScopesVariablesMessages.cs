using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class ScopesArguments
{
    [JsonPropertyName("frameId")] public int FrameId { get; set; }
}

public sealed class Scope
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("variablesReference")] public int VariablesReference { get; set; }
    [JsonPropertyName("expensive")] public bool Expensive { get; set; }
}

public sealed class ScopesResponseBody
{
    [JsonPropertyName("scopes")] public Scope[] Scopes { get; set; } = [];
}

public sealed class VariablesArguments
{
    [JsonPropertyName("variablesReference")] public int VariablesReference { get; set; }
}

public sealed class Variable
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("variablesReference")] public int VariablesReference { get; set; }
}

public sealed class VariablesResponseBody
{
    [JsonPropertyName("variables")] public Variable[] Variables { get; set; } = [];
}
