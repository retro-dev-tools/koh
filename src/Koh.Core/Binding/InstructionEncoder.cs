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

        var operandGreens = new List<GreenNodeBase>();
        for (int i = 1; i < greenNode.ChildCount; i++)
        {
            var child = greenNode.GetChild(i)!;
            if (child is GreenToken t && t.Kind == SyntaxKind.CommaToken)
                continue;
            if (child is GreenNode n && IsOperandKind(n.Kind))
                operandGreens.Add(n);
        }

        var patterns = operandGreens.Select(OperandPatternMatcher.PatternOf).ToList();

        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0);
        var values = operandGreens.Select(op =>
        {
            var expr = GetOperandExpressionFromGreen(op);
            return expr != null ? evaluator.TryEvaluate(expr) : null;
        }).ToList();

        return OperandPatternMatcher.Match(mnemonic, patterns, values);
    }

    /// <summary>
    /// Encode an instruction into the section buffer using the matched descriptor.
    /// </summary>
    public void Encode(SyntaxNode node, InstructionDescriptor desc, SectionBuffer section)
    {
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => section.CurrentPC);

        int opcodeOffset = section.CurrentOffset;

        foreach (var b in desc.Encoding)
            section.EmitByte(b);

        foreach (var rule in desc.EmitRules)
        {
            var operandGreen = GetOperandExpression(node, rule.OperandIndex);
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

    private static GreenNodeBase? GetOperandExpression(SyntaxNode instrNode, int operandIndex)
    {
        var greenNode = (GreenNode)instrNode.Green;
        int opIdx = 0;
        for (int i = 1; i < greenNode.ChildCount; i++)
        {
            var child = greenNode.GetChild(i)!;
            if (child is GreenToken t && t.Kind == SyntaxKind.CommaToken) continue;
            if (child is GreenNode n && IsOperandKind(n.Kind))
            {
                if (opIdx == operandIndex)
                    return GetOperandExpressionFromGreen(n);
                opIdx++;
            }
        }
        return null;
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
