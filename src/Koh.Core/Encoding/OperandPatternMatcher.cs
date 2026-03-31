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
            // JR targets are absolute addresses; the relative offset is computed at
            // encode time (target - PC - 2). Always match here; range check at emit.
            (OperandPattern.Imm8Signed, OperandPattern.Imm16) => true,
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
                        HasFollowingPlusMinus(green, i) switch
                        {
                            SyntaxKind.PlusToken => OperandPattern.IndHLInc,
                            SyntaxKind.MinusToken => OperandPattern.IndHLDec,
                            _ => OperandPattern.IndHL,
                        },
                    SyntaxKind.HliKeyword => OperandPattern.IndHLInc,
                    SyntaxKind.HldKeyword => OperandPattern.IndHLDec,
                    SyntaxKind.BcKeyword => OperandPattern.IndBC,
                    SyntaxKind.DeKeyword => OperandPattern.IndDE,
                    SyntaxKind.CKeyword => OperandPattern.IndC,
                    _ => OperandPattern.IndImm8,
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
                            SyntaxKind.HliKeyword => OperandPattern.IndHLInc,
                            SyntaxKind.HldKeyword => OperandPattern.IndHLDec,
                            SyntaxKind.BcKeyword => OperandPattern.IndBC,
                            SyntaxKind.DeKeyword => OperandPattern.IndDE,
                            SyntaxKind.CKeyword => OperandPattern.IndC,
                            _ => OperandPattern.IndImm16,
                        };
                    }
                }

                // Check for [$FF00 + C] pattern — this is IndC (LDH [C])
                if (exprNode.Kind == SyntaxKind.BinaryExpression && IsFF00PlusC(exprNode))
                    return OperandPattern.IndC;

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
        var green = (GreenNode)node;
        if (green.ChildCount > 0)
        {
            var expr = green.GetChild(0);
            if (IsSpPlusExpression(expr))
                return OperandPattern.SpPlusImm8;
        }
        // Default to Imm16; the matcher will narrow based on the instruction table entry
        return OperandPattern.Imm16;
    }

    /// <summary>
    /// Detect expressions of the form SP + expr or SP - expr (for LD HL, SP+imm8).
    /// </summary>
    private static bool IsSpPlusExpression(GreenNodeBase? expr)
    {
        if (expr is not GreenNode gn || gn.Kind != SyntaxKind.BinaryExpression)
            return false;
        // BinaryExpression: [left, operator, right]
        if (gn.ChildCount < 3) return false;
        var left = gn.GetChild(0);
        if (left is GreenNode nameExpr && nameExpr.Kind == SyntaxKind.NameExpression)
        {
            var inner = nameExpr.GetChild(0) as GreenToken;
            if (inner?.Kind == SyntaxKind.SpKeyword)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Extract the offset expression from SP ± expr. Returns the right-hand side
    /// of the binary expression, negated if the operator is minus.
    /// For SP + $10: returns the $10 literal expression.
    /// For SP - $46: returns the -$46 unary expression (or wraps in one).
    /// </summary>
    /// <summary>
    /// Detect [$FF00 + C] or [$FF00+C] patterns — these are LDH [C] shorthand.
    /// </summary>
    private static bool IsFF00PlusC(GreenNode binExpr)
    {
        if (binExpr.ChildCount < 3) return false;
        var op = binExpr.GetChild(1) as GreenToken;
        if (op?.Kind != SyntaxKind.PlusToken) return false;

        // Check left side evaluates to $FF00
        if (!IsFF00Literal(binExpr.GetChild(0))) return false;

        // Check right side is C register
        return IsCRegister(binExpr.GetChild(2));
    }

    private static bool IsFF00Literal(GreenNodeBase? node)
    {
        GreenToken? token = node as GreenToken;
        if (token == null && node is GreenNode gn)
        {
            if (gn.Kind == SyntaxKind.LiteralExpression)
                token = gn.GetChild(0) as GreenToken;
        }
        if (token?.Kind != SyntaxKind.NumberLiteral) return false;
        var text = token.Text.Trim();
        // Parse the number and check if it equals 0xFF00
        if (text.StartsWith('$'))
            return int.TryParse(text[1..], System.Globalization.NumberStyles.HexNumber, null, out var v) && v == 0xFF00;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out var v2) && v2 == 0xFF00;
        return int.TryParse(text, out var v3) && v3 == 0xFF00;
    }

    private static bool IsCRegister(GreenNodeBase? node)
    {
        if (node is GreenToken t)
            return t.Kind == SyntaxKind.CKeyword;
        if (node is GreenNode gn && gn.Kind == SyntaxKind.NameExpression)
        {
            var inner = gn.GetChild(0) as GreenToken;
            return inner?.Kind == SyntaxKind.CKeyword;
        }
        return false;
    }

    public static GreenNodeBase? ExtractSpOffsetExpression(GreenNodeBase operandGreen)
    {
        if (operandGreen is not GreenNode imm || imm.Kind != SyntaxKind.ImmediateOperand)
            return null;
        var expr = imm.GetChild(0) as GreenNode;
        if (expr == null || expr.Kind != SyntaxKind.BinaryExpression || expr.ChildCount < 3)
            return null;
        var op = expr.GetChild(1) as GreenToken;
        var right = expr.GetChild(2);
        if (right == null) return null;

        if (op?.Kind == SyntaxKind.MinusToken)
        {
            // Wrap in unary negation: -(right)
            return new GreenNode(SyntaxKind.UnaryExpression,
                [new GreenToken(SyntaxKind.MinusToken, "-"), right]);
        }
        return right;
    }
}
