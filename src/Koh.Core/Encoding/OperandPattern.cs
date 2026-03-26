namespace Koh.Core.Encoding;

/// <summary>
/// Describes the expected operand type for pattern matching against syntax tree operand nodes.
/// </summary>
public enum OperandPattern : byte
{
    // Individual registers
    RegA, RegB, RegC, RegD, RegE, RegH, RegL,
    // Register pairs
    RegAF, RegBC, RegDE, RegHL, RegSP,
    // Indirect (memory) addressing
    IndHL,       // [HL]
    IndBC,       // [BC]
    IndDE,       // [DE]
    IndHLInc,    // [HL+]
    IndHLDec,    // [HL-]
    IndC,        // [$FF00+C]
    // Immediates
    Imm8,        // 8-bit unsigned immediate
    Imm16,       // 16-bit immediate
    Imm8Signed,  // signed 8-bit (JR offset)
    Imm3,        // 3-bit bit index (0-7)
    IndImm8,     // [n] — high-page indirect [$FF00+n]
    IndImm16,    // [nn] — absolute indirect
    // Condition codes
    CondNZ, CondZ, CondNC, CondC,
    // RST vectors (encoded in opcode, not as immediate bytes)
    RstVec,
    // SP+r8 for LD HL, SP+r8
    SpPlusImm8,
}
