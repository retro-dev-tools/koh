using Koh.Core.Diagnostics;
using Koh.Core.Encoding;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Binding;

/// <summary>
/// Matches instruction syntax nodes against the SM83 instruction table and
/// encodes them into section byte buffers.
/// </summary>
internal sealed class InstructionEncoder
{
    private readonly SymbolTable _symbols;
    private readonly DiagnosticBag _diagnostics;

    public InstructionEncoder(SymbolTable symbols, DiagnosticBag diagnostics)
    {
        _symbols = symbols;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Match an instruction node against the SM83 table. Returns null if no encoding matches.
    /// </summary>
    public InstructionDescriptor? Match(SyntaxNode node)
    {
        var greenNode = (GreenNode)node.Green;
        var mnemonicToken = (GreenToken)greenNode.GetChild(0)!;
        var mnemonic = mnemonicToken.Text;

        // SM83 instructions have at most 3 operands — use fixed-size arrays to avoid LINQ allocations
        var operandGreens = new GreenNodeBase?[3];
        int operandCount = 0;
        for (int i = 1; i < greenNode.ChildCount && operandCount < 3; i++)
        {
            var child = greenNode.GetChild(i)!;
            if (child is GreenToken t && t.Kind == SyntaxKind.CommaToken)
                continue;
            if (child is GreenNode n && IsOperandKind(n.Kind))
                operandGreens[operandCount++] = n;
        }

        var patterns = new OperandPattern[operandCount];
        var values = new long?[operandCount];
        for (int i = 0; i < operandCount; i++)
            patterns[i] = OperandPatternMatcher.PatternOf(operandGreens[i]!);

        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0);
        for (int i = 0; i < operandCount; i++)
        {
            var op = operandGreens[i]!;
            // SpPlusImm8: evaluate only the offset, not the full SP±expr
            // (avoids creating a spurious forward ref for register keyword 'sp')
            if (patterns[i] == OperandPattern.SpPlusImm8)
            {
                var offsetExpr = OperandPatternMatcher.ExtractSpOffsetExpression(op);
                values[i] = offsetExpr != null ? evaluator.TryEvaluate(offsetExpr) : null;
            }
            else
            {
                var expr = GetOperandExpressionFromGreen(op);
                values[i] = expr != null ? evaluator.TryEvaluate(expr) : null;
            }
        }

        return OperandPatternMatcher.Match(mnemonic, patterns, values);
    }

    /// <summary>
    /// Encode an instruction into the section buffer using the matched descriptor.
    /// </summary>
    public void Encode(SyntaxNode node, InstructionDescriptor desc, SectionBuffer section)
    {
        int instructionPC = section.CurrentPC;
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => instructionPC);

        int opcodeOffset = section.CurrentOffset;

        foreach (var b in desc.Encoding)
            section.EmitByte(b);

        foreach (var rule in desc.EmitRules)
        {
            var operandGreen = GetOperandExpression(node, rule.OperandIndex);

            // LD HL, SP+imm8: extract just the offset, not the full SP±expr
            if (rule.OperandIndex < desc.Operands.Length
                && desc.Operands[rule.OperandIndex] == OperandPattern.SpPlusImm8)
            {
                var rawOperand = GetRawOperand(node, rule.OperandIndex);
                if (rawOperand != null)
                    operandGreen = OperandPatternMatcher.ExtractSpOffsetExpression(rawOperand);
            }

            var value = operandGreen != null ? evaluator.TryEvaluate(operandGreen) : null;

            switch (rule.Kind)
            {
                case EmitRuleKind.AppendImm8:
                    if (value.HasValue)
                        section.EmitByte((byte)(value.Value & 0xFF));
                    else
                    {
                        int offset = section.ReserveByte();
                        if (operandGreen != null)
                            section.RecordPatch(new PatchEntry
                            {
                                SectionName = section.Name,
                                Offset = offset,
                                Expression = operandGreen,
                                Kind = PatchKind.Absolute8,
                                FilePath = _diagnostics.CurrentFilePath,
                                GlobalAnchorName = _symbols.CurrentGlobalAnchorName,
                            });
                    }
                    break;

                case EmitRuleKind.AppendImm16LE:
                    if (value.HasValue)
                        section.EmitWord((ushort)(value.Value & 0xFFFF));
                    else
                    {
                        int offset = section.ReserveWord();
                        if (operandGreen != null)
                            section.RecordPatch(new PatchEntry
                            {
                                SectionName = section.Name,
                                Offset = offset,
                                Expression = operandGreen,
                                Kind = PatchKind.Absolute16,
                                FilePath = _diagnostics.CurrentFilePath,
                                GlobalAnchorName = _symbols.CurrentGlobalAnchorName,
                            });
                    }
                    break;

                case EmitRuleKind.AppendRelative8:
                    if (value.HasValue)
                    {
                        long rel = value.Value - (section.CurrentPC + 1);
                        if (rel < -128 || rel > 127)
                        {
                            _diagnostics.Report(node.FullSpan,
                                $"JR target out of range: offset {rel} does not fit in signed byte");
                            section.EmitByte(0x00);
                        }
                        else
                        {
                            section.EmitByte((byte)(sbyte)rel);
                        }
                    }
                    else
                    {
                        int offset = section.ReserveByte();
                        if (operandGreen != null)
                            section.RecordPatch(new PatchEntry
                            {
                                SectionName = section.Name,
                                Offset = offset,
                                Expression = operandGreen,
                                Kind = PatchKind.Relative8,
                                PCAfterInstruction = section.CurrentPC,
                                FilePath = _diagnostics.CurrentFilePath,
                                GlobalAnchorName = _symbols.CurrentGlobalAnchorName,
                            });
                    }
                    break;

                case EmitRuleKind.OpcodeOrImm8:
                    if (value.HasValue)
                        section.ApplyPatch(opcodeOffset, (byte)(desc.Encoding[0] | (value.Value & 0xFF)));
                    else
                        _diagnostics.Report(node.FullSpan,
                            "RST vector must be a constant expression");
                    break;
            }
        }
    }

    /// <summary>Get the raw operand green node (e.g. ImmediateOperand) at the given index.</summary>
    private static GreenNodeBase? GetRawOperand(SyntaxNode instrNode, int operandIndex)
    {
        var greenNode = (GreenNode)instrNode.Green;
        int opIdx = 0;
        for (int i = 1; i < greenNode.ChildCount; i++)
        {
            var child = greenNode.GetChild(i)!;
            if (child is GreenToken t && t.Kind == SyntaxKind.CommaToken) continue;
            if (child is GreenNode n && IsOperandKind(n.Kind))
            {
                if (opIdx == operandIndex) return n;
                opIdx++;
            }
        }
        return null;
    }

    private static GreenNodeBase? GetOperandExpression(SyntaxNode instrNode, int operandIndex)
    {
        var raw = GetRawOperand(instrNode, operandIndex);
        return raw != null ? GetOperandExpressionFromGreen(raw) : null;
    }

    private static GreenNodeBase? GetOperandExpressionFromGreen(GreenNodeBase operand)
    {
        if (operand is not GreenNode green) return null;
        return green.Kind switch
        {
            SyntaxKind.ImmediateOperand => green.GetChild(0),
            SyntaxKind.LabelOperand => green.GetChild(0),
            SyntaxKind.IndirectOperand => GetIndirectExpression(green),
            _ => null,
        };
    }

    private static GreenNodeBase? GetIndirectExpression(GreenNode indirect)
    {
        if (indirect.ChildCount < 3) return null;
        var inner = indirect.GetChild(1);

        if (inner is not GreenNode exprNode) return null;

        if (exprNode.Kind == SyntaxKind.NameExpression)
        {
            var tok = exprNode.GetChild(0) as GreenToken;
            if (tok != null && IsRegisterOrPairKeyword(tok.Kind))
                return null;
        }

        return exprNode;
    }

    private static bool IsRegisterOrPairKeyword(SyntaxKind kind) =>
        kind is >= SyntaxKind.AKeyword and <= SyntaxKind.DeKeyword;

    private static bool IsOperandKind(SyntaxKind kind) => kind is
        SyntaxKind.RegisterOperand or SyntaxKind.ImmediateOperand or
        SyntaxKind.IndirectOperand or SyntaxKind.ConditionOperand or
        SyntaxKind.LabelOperand;
}
