using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Encoding;

/// <summary>
/// Converts syntax tree operand nodes into OperandPattern values and matches
/// against the instruction table to find the correct encoding.
/// </summary>
public static class OperandPatternMatcher
{
    public static InstructionDescriptor? Match(string mnemonic,
        IReadOnlyList<OperandPattern> patterns, IReadOnlyList<long?> values)
    {
        foreach (var desc in Sm83InstructionTable.Lookup(mnemonic))
        {
            if (desc.Operands.Length != patterns.Count)
                continue;
            if (PatternsMatch(desc, patterns, values))
                return desc;
        }
        return null;
    }

    private static bool PatternsMatch(InstructionDescriptor desc,
        IReadOnlyList<OperandPattern> actual, IReadOnlyList<long?> values)
    {
        var expected = desc.Operands;
        for (int i = 0; i < expected.Length; i++)
        {
            if (!SingleMatch(expected[i], actual[i], values.Count > i ? values[i] : null))
                return false;
        }

        // BIT/SET/RES: verify the Imm3 operand value matches the bit index baked into
        // this specific descriptor. Without this check, "BIT 3, A" would match the
        // first entry in the lookup (bit 0) because all 8 entries share the Imm3 pattern.
        if (desc.ExpectedBitIndex.HasValue)
        {
            var bitValue = values.Count > 0 ? values[0] : null;
            if (bitValue == null || bitValue.Value != desc.ExpectedBitIndex.Value)
                return false;
        }

        return true;
    }

    private static bool SingleMatch(OperandPattern expected, OperandPattern actual, long? value)
    {
        if (expected == actual) return true;

        // Immediate widening: actual is an immediate, expected accepts it
        return (expected, actual) switch
        {
            (OperandPattern.Imm8, OperandPattern.Imm16) =>
                value == null || (value >= -128 && value <= 255),
            (OperandPattern.Imm8, OperandPattern.Imm8Signed) => true,
            (OperandPattern.Imm8Signed, OperandPattern.Imm16) =>
                value == null || (value >= -128 && value <= 127),
            (OperandPattern.Imm8Signed, OperandPattern.Imm8) =>
                value == null || (value >= -128 && value <= 127),
            (OperandPattern.Imm16, OperandPattern.Imm8) => true, // 8-bit fits in 16-bit
            (OperandPattern.Imm3, OperandPattern.Imm8) =>
                value == null || (value >= 0 && value <= 7),
            (OperandPattern.Imm3, OperandPattern.Imm16) =>
                value == null || (value >= 0 && value <= 7),
            (OperandPattern.RstVec, OperandPattern.Imm8) =>
                value != null && IsRstVector(value.Value),
            (OperandPattern.RstVec, OperandPattern.Imm16) =>
                value != null && IsRstVector(value.Value),
            // IndirectPattern always returns IndImm16 for expression-based indirects.
            // LDH uses IndImm8 in the table, so we must accept IndImm16 as IndImm8.
            // The value range constraint (0x00–0xFF) is not checked here because the
            // LDH opcode encodes the low byte regardless; out-of-range values would be
            // caught as a separate diagnostic pass.
            (OperandPattern.IndImm8, OperandPattern.IndImm16) => true,
            // The parser always emits C as RegisterOperand(C) — it never produces
            // ConditionOperand(C) because C is ambiguous at parse time. The pattern
            // matcher must accept RegC where CondC is expected (JR C, / RET C / etc.).
            (OperandPattern.CondC, OperandPattern.RegC) => true,
            _ => false,
        };
    }

    private static bool IsRstVector(long value) =>
        value is 0x00 or 0x08 or 0x10 or 0x18 or 0x20 or 0x28 or 0x30 or 0x38;

    /// <summary>
    /// Convert a syntax tree operand node into an OperandPattern.
    /// </summary>
    public static OperandPattern PatternOf(GreenNodeBase operand)
    {
        return operand.Kind switch
        {
            SyntaxKind.RegisterOperand => RegisterPattern(operand),
            SyntaxKind.ConditionOperand => ConditionPattern(operand),
            SyntaxKind.IndirectOperand => IndirectPattern(operand),
            SyntaxKind.ImmediateOperand => ImmediatePattern(operand),
            SyntaxKind.LabelOperand => OperandPattern.Imm16, // labels are 16-bit addresses
            _ => OperandPattern.Imm16,
        };
    }

    private static OperandPattern RegisterPattern(GreenNodeBase node)
    {
        var token = (GreenToken)((GreenNode)node).GetChild(0)!;
        return token.Kind switch
        {
            SyntaxKind.AKeyword => OperandPattern.RegA,
            SyntaxKind.BKeyword => OperandPattern.RegB,
            SyntaxKind.CKeyword => OperandPattern.RegC,
            SyntaxKind.DKeyword => OperandPattern.RegD,
            SyntaxKind.EKeyword => OperandPattern.RegE,
            SyntaxKind.HKeyword => OperandPattern.RegH,
            SyntaxKind.LKeyword => OperandPattern.RegL,
            SyntaxKind.HlKeyword => OperandPattern.RegHL,
            SyntaxKind.SpKeyword => OperandPattern.RegSP,
            SyntaxKind.AfKeyword => OperandPattern.RegAF,
            SyntaxKind.BcKeyword => OperandPattern.RegBC,
            SyntaxKind.DeKeyword => OperandPattern.RegDE,
            _ => OperandPattern.RegA, // fallback
        };
    }

    private static OperandPattern ConditionPattern(GreenNodeBase node)
    {
        var token = (GreenToken)((GreenNode)node).GetChild(0)!;
        return token.Kind switch
        {
            SyntaxKind.ZKeyword => OperandPattern.CondZ,
            SyntaxKind.NzKeyword => OperandPattern.CondNZ,
            SyntaxKind.NcKeyword => OperandPattern.CondNC,
            SyntaxKind.CKeyword => OperandPattern.CondC,
            _ => OperandPattern.CondNZ,
        };
    }

    private static OperandPattern IndirectPattern(GreenNodeBase node)
    {
        var green = (GreenNode)node;
        // Children: [ content... ]
        // Check for [HL], [BC], [DE], [HL+], [HL-], [C]

        // Find the inner content (skip [ and ])
        for (int i = 1; i < green.ChildCount - 1; i++)
        {
            var child = green.GetChild(i)!;

            // Flat tokens: HL, BC, DE, C, +, -
            if (child is GreenToken token)
            {
                return token.Kind switch
                {
                    SyntaxKind.HlKeyword =>
                        // Check for [HL+] or [HL-]
                        HasFollowingPlusMinus(green, i) switch
                        {
                            SyntaxKind.PlusToken => OperandPattern.IndHLInc,
                            SyntaxKind.MinusToken => OperandPattern.IndHLDec,
                            _ => OperandPattern.IndHL,
                        },
                    SyntaxKind.BcKeyword => OperandPattern.IndBC,
                    SyntaxKind.DeKeyword => OperandPattern.IndDE,
                    SyntaxKind.CKeyword => OperandPattern.IndC,
                    _ => OperandPattern.IndImm8, // unknown token inside brackets
                };
            }

            // Expression node inside brackets (parsed as expression by the Pratt parser)
            if (child is GreenNode exprNode)
            {
                // Check if it's a NameExpression wrapping a register keyword
                if (exprNode.Kind == SyntaxKind.NameExpression)
                {
                    var innerToken = exprNode.GetChild(0) as GreenToken;
                    if (innerToken != null)
                    {
                        return innerToken.Kind switch
                        {
                            SyntaxKind.HlKeyword => OperandPattern.IndHL,
                            SyntaxKind.BcKeyword => OperandPattern.IndBC,
                            SyntaxKind.DeKeyword => OperandPattern.IndDE,
                            SyntaxKind.CKeyword => OperandPattern.IndC,
                            _ => OperandPattern.IndImm16,
                        };
                    }
                }

                // Expression inside brackets — could be 8-bit or 16-bit
                // For LDH: [n] is IndImm8. For LD: [nn] is IndImm16.
                // The mnemonic determines which — the matcher handles this.
                return OperandPattern.IndImm16;
            }
        }

        return OperandPattern.IndImm16;
    }

    private static SyntaxKind HasFollowingPlusMinus(GreenNode parent, int index)
    {
        if (index + 1 < parent.ChildCount - 1) // -1 to skip closing ]
        {
            var next = parent.GetChild(index + 1);
            if (next is GreenToken t)
                return t.Kind;
        }
        return SyntaxKind.None;
    }

    private static OperandPattern ImmediatePattern(GreenNodeBase node)
    {
        // ImmediateOperand wraps an expression node
        // Default to Imm16; the matcher will narrow based on the instruction table entry
        return OperandPattern.Imm16;
    }
}
