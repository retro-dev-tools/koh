using System.Text.Json.Serialization;

namespace Koh.Debugger.Dap.Messages;

public sealed class DisassembleArguments
{
    [JsonPropertyName("memoryReference")] public string MemoryReference { get; set; } = "";
    [JsonPropertyName("offset")] public int Offset { get; set; }
    [JsonPropertyName("instructionOffset")] public int InstructionOffset { get; set; }
    [JsonPropertyName("instructionCount")] public int InstructionCount { get; set; }
    [JsonPropertyName("resolveSymbols")] public bool ResolveSymbols { get; set; }
}

public sealed class DisassembleResponseBody
{
    [JsonPropertyName("instructions")] public DisassembledInstruction[] Instructions { get; set; } = Array.Empty<DisassembledInstruction>();
}

public sealed class DisassembledInstruction
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("instructionBytes")] public string? InstructionBytes { get; set; }
    [JsonPropertyName("instruction")] public string Instruction { get; set; } = "";
}
