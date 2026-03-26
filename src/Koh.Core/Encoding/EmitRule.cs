namespace Koh.Core.Encoding;

/// <summary>
/// Describes how to embed an operand value into the emitted byte stream.
/// </summary>
public enum EmitRuleKind : byte
{
    /// <summary>Append 1 byte — low 8 bits of evaluated expression.</summary>
    AppendImm8,
    /// <summary>Append 2 bytes — little-endian 16-bit.</summary>
    AppendImm16LE,
    /// <summary>Append 1 byte — PC-relative signed offset (for JR).</summary>
    AppendRelative8,
    /// <summary>
    /// OR the operand value into the first opcode byte already written.
    /// Used by RST: the vector (0x00/0x08/.../0x38) is ORed into the base 0xC7.
    /// </summary>
    OpcodeOrImm8,
}

public readonly struct EmitRule
{
    public EmitRuleKind Kind { get; init; }
    public int OperandIndex { get; init; }

    public EmitRule(EmitRuleKind kind, int operandIndex)
    {
        Kind = kind;
        OperandIndex = operandIndex;
    }
}
