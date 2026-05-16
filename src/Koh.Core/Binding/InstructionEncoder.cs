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
            // SpPlusImm8 / IndC: don't evaluate the full expression — it contains
            // register keywords that aren't numeric symbols.
            if (patterns[i] == OperandPattern.SpPlusImm8)
            {
                var offsetExpr = OperandPatternMatcher.ExtractSpOffsetExpression(op);
                values[i] = offsetExpr != null ? evaluator.TryEvaluate(offsetExpr) : null;
            }
            else if (patterns[i] == OperandPattern.IndC)
            {
                values[i] = null; // no immediate value for [C] / [$FF00+C]
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
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => instructionPC,
            section.Name, section.BaseAddress, currentSectionIsFloating: section.FixedAddress == null);

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
                    {
                        // LDH requires the address to be in $FF00–$FFFF.
                        // A value of $00–$FF is not acceptable — the programmer must write the
                        // full address (e.g. LDH [$FF80], A rather than LDH [$80], A).
                        if (desc.Mnemonic.Equals("LDH", StringComparison.OrdinalIgnoreCase))
                        {
                            long addr = value.Value;
                            if (addr < 0xFF00 || addr > 0xFFFF)
                            {
                                _diagnostics.Report(node.FullSpan,
                                    $"LDH address ${addr:X4} is not in range $FF00–$FFFF");
                            }
                        }
                        section.EmitByte((byte)(value.Value & 0xFF));
                    }
                    else
                    {
                        int offset = section.ReserveByte();
                        if (operandGreen != null)
                        {
                            var (sn, so, sh) = ExtractIdentifierAndOffset(operandGreen);
                            section.RecordPatch(new PatchEntry
                            {
                                SectionName = section.Name,
                                Offset = offset,
                                Expression = operandGreen,
                                Kind = PatchKind.Absolute8,
                                FilePath = _diagnostics.CurrentFilePath,
                                GlobalAnchorName = _symbols.CurrentGlobalAnchorName,
                                SymbolName = sn,
                                SymbolOffset = so,
                                SymbolShift = sh,
                            });
                        }
                    }
                    break;

                case EmitRuleKind.AppendImm16LE:
                    if (value.HasValue)
                        section.EmitWord((ushort)(value.Value & 0xFFFF));
                    else
                    {
                        int offset = section.ReserveWord();
                        if (operandGreen != null)
                        {
                            var (sn, so, sh) = ExtractIdentifierAndOffset(operandGreen);
                            section.RecordPatch(new PatchEntry
                            {
                                SectionName = section.Name,
                                Offset = offset,
                                Expression = operandGreen,
                                Kind = PatchKind.Absolute16,
                                FilePath = _diagnostics.CurrentFilePath,
                                GlobalAnchorName = _symbols.CurrentGlobalAnchorName,
                                SymbolName = sn,
                                SymbolOffset = so,
                                SymbolShift = sh,
                            });
                        }
                    }
                    break;

                case EmitRuleKind.AppendRelative8:
                    if (value.HasValue)
                    {
                        // value.Value is an absolute address (evaluator already adds section base).
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
                        {
                            var (sn, so, sh) = ExtractIdentifierAndOffset(operandGreen);
                            section.RecordPatch(new PatchEntry
                            {
                                SectionName = section.Name,
                                Offset = offset,
                                Expression = operandGreen,
                                Kind = PatchKind.Relative8,
                                // Store section-relative offset of the byte after this instruction.
                                // PatchResolver adds section.BaseAddress to recover absolute PC.
                                PCAfterInstruction = section.CurrentOffset,
                                FilePath = _diagnostics.CurrentFilePath,
                                GlobalAnchorName = _symbols.CurrentGlobalAnchorName,
                                SymbolName = sn,
                                SymbolOffset = so,
                                SymbolShift = sh,
                            });
                        }
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

    /// <summary>
    /// Returns the identifier name if <paramref name="expression"/> is a bare identifier.
    /// Handles both a <see cref="SyntaxKind.NameExpression"/> (wrapping a single token)
    /// and a raw <see cref="SyntaxKind.IdentifierToken"/> or
    /// <see cref="SyntaxKind.LocalLabelToken"/> (produced by LabelOperand unwrapping).
    /// Returns <c>null</c> for complex expressions (binary, unary, function calls, etc.).
    /// </summary>
    private string? ExtractSingleIdentifier(GreenNodeBase expression) =>
        ExtractIdentifierAndOffset(expression).name;

    /// <summary>
    /// Recognises <c>Name</c>, <c>Name + N</c>, <c>N + Name</c>, <c>Name - N</c>,
    /// <c>HIGH(...)</c>, and <c>LOW(...)</c> patterns (where <c>...</c> is one of
    /// the supported simple forms). Returns the qualified symbol name (local
    /// labels are prefixed with the current global anchor), the signed integer
    /// offset to add, and a right-shift to apply to the resolved value
    /// (8 for <c>HIGH</c>, 0 otherwise). Returns <c>(null, 0, 0)</c> for anything
    /// else.
    ///
    /// This lets the linker resolve operands like <c>ldh [hScratch+4], a</c> and
    /// <c>cp HIGH(.pow10_end)</c> when the label lives in a different section than
    /// the calling code: the per-byte expression evaluator can't combine a
    /// cross-section label with a constant, so we record the parts and the
    /// linker computes <c>((sym.AbsoluteAddress + offset) &gt;&gt; shift) &amp; 0xFF</c>
    /// (or the 16-bit equivalent) at patch time.
    /// </summary>
    private (string? name, int offset, int shift) ExtractIdentifierAndOffset(GreenNodeBase expression)
    {
        // HIGH(arg) / LOW(arg)
        if (expression is GreenNode call && call.Kind == SyntaxKind.FunctionCallExpression
            && call.ChildCount >= 1 && call.GetChild(0) is GreenToken kw)
        {
            int shift = kw.Kind switch
            {
                SyntaxKind.HighKeyword => 8,
                SyntaxKind.LowKeyword => 0,
                _ => -1,
            };
            if (shift >= 0)
            {
                // Locate the single argument inside the call.
                GreenNodeBase? arg = null;
                for (int i = 1; i < call.ChildCount; i++)
                {
                    var c = call.GetChild(i);
                    if (c is GreenToken t && t.Kind is SyntaxKind.OpenParenToken
                        or SyntaxKind.CloseParenToken or SyntaxKind.CommaToken)
                        continue;
                    arg = c; break;
                }
                if (arg != null)
                {
                    var inner = ExtractIdentifierAndOffset(arg);
                    if (inner.name != null)
                        return (inner.name, inner.offset, kw.Kind == SyntaxKind.HighKeyword ? 8 : 0);
                }
            }
        }

        // Plain identifier or NameExpression wrapping one.
        var direct = ExtractSingleIdentifierText(expression);
        if (direct != null)
            return (QualifyLocal(direct), 0, 0);

        if (expression is not GreenNode binExpr) return (null, 0, 0);
        if (binExpr.Kind != SyntaxKind.BinaryExpression) return (null, 0, 0);
        if (binExpr.ChildCount != 3) return (null, 0, 0);

        var left = binExpr.GetChild(0);
        var op = binExpr.GetChild(1) as GreenToken;
        var right = binExpr.GetChild(2);
        if (op is null) return (null, 0, 0);

        // Try (Name) op (Number).
        var nameLeft = left != null ? ExtractSingleIdentifierText(left) : null;
        var numRight = right != null ? TryParseConstant(right) : null;
        if (nameLeft != null && numRight.HasValue)
        {
            if (op.Kind == SyntaxKind.PlusToken)
                return (QualifyLocal(nameLeft), checked((int)numRight.Value), 0);
            if (op.Kind == SyntaxKind.MinusToken)
                return (QualifyLocal(nameLeft), checked((int)(-numRight.Value)), 0);
        }

        // Try (Number) + (Name). Subtraction with the name on the right doesn't
        // produce a "label + offset" pattern (it's "constant - label"), so skip.
        var numLeft = left != null ? TryParseConstant(left) : null;
        var nameRight = right != null ? ExtractSingleIdentifierText(right) : null;
        if (numLeft.HasValue && nameRight != null && op.Kind == SyntaxKind.PlusToken)
            return (QualifyLocal(nameRight), checked((int)numLeft.Value), 0);

        return (null, 0, 0);
    }

    private string QualifyLocal(string text)
    {
        if (!text.StartsWith('.')) return text;
        var anchor = _symbols.CurrentGlobalAnchorName;
        return anchor != null ? string.Concat(anchor, text) : text;
    }

    /// <summary>
    /// Parses a constant number literal (raw NumberLiteral token or a
    /// NameExpression that resolves to an EQU constant). Returns null otherwise.
    /// </summary>
    private long? TryParseConstant(GreenNodeBase node)
    {
        if (node is GreenToken tok && tok.Kind == SyntaxKind.NumberLiteral)
            return ParseNumberLiteral(tok.Text);

        if (node is GreenNode literalNode && literalNode.Kind == SyntaxKind.LiteralExpression
            && literalNode.ChildCount == 1
            && literalNode.GetChild(0) is GreenToken litTok
            && litTok.Kind == SyntaxKind.NumberLiteral)
            return ParseNumberLiteral(litTok.Text);

        // EQU constants are stored in the symbol table without a Section.
        if (node is GreenNode nameNode && nameNode.Kind == SyntaxKind.NameExpression
            && nameNode.ChildCount == 1
            && nameNode.GetChild(0) is GreenToken nameTok
            && nameTok.Kind is SyntaxKind.IdentifierToken)
        {
            var sym = _symbols.Lookup(nameTok.Text);
            if (sym is not null && sym.Section == null
                && sym.State == Koh.Core.Symbols.SymbolState.Defined)
                return sym.Value;
        }

        return null;
    }

    private static long? ParseNumberLiteral(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            if (text.StartsWith('$'))
                return Convert.ToInt64(text[1..].Replace("_", ""), 16);
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt64(text[2..].Replace("_", ""), 16);
            if (text.StartsWith('%'))
                return Convert.ToInt64(text[1..].Replace("_", ""), 2);
            if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                return Convert.ToInt64(text[2..].Replace("_", ""), 2);
            if (text.StartsWith('&'))
                return Convert.ToInt64(text[1..].Replace("_", ""), 8);
            return long.Parse(text.Replace("_", ""), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractSingleIdentifierText(GreenNodeBase expression)
    {
        // Raw token (e.g. child of LabelOperand after GetChild(0))
        if (expression is GreenToken rawToken)
        {
            if (rawToken.Kind is SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken)
                return rawToken.Text;
            return null;
        }

        if (expression is not GreenNode nameExpr) return null;
        if (nameExpr.Kind != SyntaxKind.NameExpression) return null;
        if (nameExpr.ChildCount != 1) return null;
        var token = nameExpr.GetChild(0) as GreenToken;
        if (token is null) return null;
        if (token.Kind is not (SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken))
            return null;
        return token.Text;
    }
}
