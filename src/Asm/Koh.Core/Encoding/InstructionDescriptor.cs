namespace Koh.Core.Encoding;

/// <summary>
/// A single SM83 instruction encoding: mnemonic + operand pattern → opcode bytes + emit rules.
/// </summary>
public sealed class InstructionDescriptor
{
    public required string Mnemonic { get; init; }
    public required OperandPattern[] Operands { get; init; }
    public required byte[] Encoding { get; init; }
    public required int Size { get; init; }
    public EmitRule[] EmitRules { get; init; } = [];

    /// <summary>
    /// For BIT/SET/RES instructions, the exact bit index (0–7) baked into this entry's opcode.
    /// The pattern matcher enforces that the Imm3 operand value equals this field exactly.
    /// Null for all other instructions.
    /// </summary>
    public int? ExpectedBitIndex { get; init; }
}
