using Koh.Core.Diagnostics;
using Koh.Core.Encoding;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Binding;

public sealed class Binder
{
    private readonly DiagnosticBag _diagnostics = new();
    private readonly SymbolTable _symbols;
    private readonly SectionManager _sections = new();

    public Binder()
    {
        _symbols = new SymbolTable(_diagnostics);
    }

    public BindingResult Bind(SyntaxTree tree)
    {
        // Merge parser diagnostics
        foreach (var d in tree.Diagnostics)
            _diagnostics.Report(d.Span, d.Message, d.Severity);

        // Pass 1: collect symbols and track PCs
        var pcTracker = new SectionPCTracker();
        Pass1(tree.Root, pcTracker);

        // Pass 2: emit bytes and encode instructions
        Pass2(tree.Root);

        // Resolve patches (forward refs now defined after Pass 1)
        new PatchResolver(_symbols, _sections, _diagnostics).ApplyAll();

        // Check for undefined symbols (after all passes so data directive refs are included)
        foreach (var sym in _symbols.GetUndefinedSymbols())
            _diagnostics.Report(default, $"Undefined symbol '{sym.Name}'");

        return new BindingResult(
            _sections.AllSections,
            _symbols,
            _diagnostics.ToList());
    }

    /// <summary>
    /// Bind and produce a frozen EmitModel for the linker / .kobj writer.
    /// </summary>
    public EmitModel BindToEmitModel(SyntaxTree tree)
    {
        var result = Bind(tree);
        return EmitModel.FromBindingResult(result);
    }

    private void Pass1(SyntaxNode root, SectionPCTracker pc)
    {
        foreach (var child in root.ChildNodesAndTokens())
        {
            if (!child.IsNode) continue;
            var node = child.AsNode!;

            switch (node.Kind)
            {
                case SyntaxKind.LabelDeclaration:
                    Pass1Label(node, pc);
                    break;

                case SyntaxKind.SectionDirective:
                    Pass1Section(node, pc);
                    break;

                case SyntaxKind.InstructionStatement:
                    Pass1Instruction(node, pc);
                    break;

                case SyntaxKind.DataDirective:
                    Pass1Data(node, pc);
                    break;

                case SyntaxKind.SymbolDirective:
                    Pass1Symbol(node, pc);
                    break;
            }
        }
    }

    private void Pass1Label(SyntaxNode node, SectionPCTracker pc)
    {
        var tokens = node.ChildTokens().ToList();
        var nameToken = tokens[0];
        var sym = _symbols.DefineLabel(nameToken.Text, pc.CurrentPC, pc.ActiveSectionName, node);

        // Exported label (main::) — double colon
        if (tokens.Count >= 2 && tokens[1].Kind == SyntaxKind.DoubleColonToken)
            sym.Visibility = SymbolVisibility.Exported;
    }

    private void Pass1Section(SyntaxNode node, SectionPCTracker pc)
    {
        if (!TryParseSectionHeader(node, out var name, out var sectionType))
            return;

        // TODO (Task 5.3): extract fixedAddr from flat bracket-group tokens [$addr].
        pc.SetActive(name!, 0);
    }

    private void Pass1Instruction(SyntaxNode node, SectionPCTracker pc)
    {
        var desc = MatchInstruction(node);
        if (desc != null)
            pc.Advance(desc.Size);
        else
            pc.Advance(1); // fallback for unrecognized — diagnostic in Pass 2
    }

    private void Pass1Data(SyntaxNode node, SectionPCTracker pc)
    {
        var keyword = node.ChildTokens().First();
        var expressions = node.ChildNodes().ToList();

        switch (keyword.Kind)
        {
            case SyntaxKind.DbKeyword:
                // Each expression produces 1 byte (strings produce multiple)
                pc.Advance(expressions.Count);
                break;
            case SyntaxKind.DwKeyword:
                pc.Advance(expressions.Count * 2);
                break;
            case SyntaxKind.DsKeyword:
            {
                // ds SIZE [, FILL] — first expression is the count
                var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => pc.CurrentPC);
                if (expressions.Count > 0)
                {
                    var sizeVal = evaluator.TryEvaluate(GetGreenNode(expressions[0]));
                    pc.Advance(sizeVal.HasValue ? (int)sizeVal.Value : 0);
                }
                break;
            }
        }
    }

    private void Pass1Symbol(SyntaxNode node, SectionPCTracker pc)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count < 2) return;

        var first = tokens[0];

        // identifier EQU expr
        if (first.Kind == SyntaxKind.IdentifierToken &&
            tokens.Count >= 2 && tokens[1].Kind == SyntaxKind.EquKeyword)
        {
            var exprNodes = node.ChildNodes().ToList();
            if (exprNodes.Count > 0)
            {
                var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => pc.CurrentPC);
                var value = evaluator.TryEvaluate(GetGreenNode(exprNodes[0]));
                if (value.HasValue)
                    _symbols.DefineConstant(first.Text, value.Value, node);
            }
        }
        // EXPORT sym [, sym]*
        else if (first.Kind == SyntaxKind.ExportKeyword)
        {
            for (int i = 1; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == SyntaxKind.IdentifierToken)
                {
                    // NOTE: if a local label (.name) is exported before its global anchor
                    // is established, DeclareForwardRef creates an unqualified placeholder
                    // that will never match the qualified definition. EXPORT of local labels
                    // before their enclosing global label is therefore unsupported and will
                    // produce a spurious "Undefined symbol" diagnostic. This is an acceptable
                    // limitation for Phase 5; full local-label forward-export requires
                    // deferred qualification (Task 8.x).
                    var sym = _symbols.Lookup(tokens[i].Text)
                              ?? _symbols.DeclareForwardRef(tokens[i].Text);
                    sym.Visibility = SymbolVisibility.Exported;
                }
            }
        }
        // PURGE sym [, sym]*
        else if (first.Kind == SyntaxKind.PurgeKeyword)
        {
            // PURGE removes symbols — for now just record the intent;
            // full PURGE semantics deferred to Phase 8 (macros)
        }
    }

    private void Pass2(SyntaxNode root)
    {
        foreach (var child in root.ChildNodesAndTokens())
        {
            if (!child.IsNode) continue;
            var node = child.AsNode!;

            switch (node.Kind)
            {
                case SyntaxKind.SectionDirective:
                    Pass2Section(node);
                    break;

                case SyntaxKind.DataDirective:
                    Pass2Data(node);
                    break;

                case SyntaxKind.InstructionStatement:
                    Pass2Instruction(node);
                    break;
            }
        }
    }

    private void Pass2Section(SyntaxNode node)
    {
        if (!TryParseSectionHeader(node, out var name, out var sectionType))
            return;

        _sections.OpenOrResume(name!, sectionType);
    }

    private void Pass2Data(SyntaxNode node)
    {
        var section = _sections.ActiveSection;
        if (section == null)
        {
            _diagnostics.Report(node.FullSpan, "Data directive outside of a section");
            return;
        }

        var keyword = node.ChildTokens().First();
        var expressions = node.ChildNodes().ToList();
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => section.CurrentPC);

        switch (keyword.Kind)
        {
            case SyntaxKind.DbKeyword:
                foreach (var expr in expressions)
                {
                    var val = evaluator.TryEvaluate(GetGreenNode(expr));
                    if (val.HasValue)
                        section.EmitByte((byte)(val.Value & 0xFF));
                    else
                    {
                        int offset = section.ReserveByte();
                        section.RecordPatch(new PatchEntry
                        {
                            SectionName = section.Name,
                            Offset = offset,
                            Expression = GetGreenNode(expr),
                            Kind = PatchKind.Absolute8,
                        });
                    }
                }
                break;

            case SyntaxKind.DwKeyword:
                foreach (var expr in expressions)
                {
                    var val = evaluator.TryEvaluate(GetGreenNode(expr));
                    if (val.HasValue)
                        section.EmitWord((ushort)(val.Value & 0xFFFF));
                    else
                    {
                        int offset = section.ReserveWord();
                        section.RecordPatch(new PatchEntry
                        {
                            SectionName = section.Name,
                            Offset = offset,
                            Expression = GetGreenNode(expr),
                            Kind = PatchKind.Absolute16,
                        });
                    }
                }
                break;

            case SyntaxKind.DsKeyword:
                if (expressions.Count > 0)
                {
                    var countVal = evaluator.TryEvaluate(GetGreenNode(expressions[0]));
                    byte fill = 0x00;
                    if (expressions.Count > 1)
                    {
                        var fillVal = evaluator.TryEvaluate(GetGreenNode(expressions[1]));
                        if (fillVal.HasValue) fill = (byte)(fillVal.Value & 0xFF);
                    }
                    if (countVal.HasValue)
                        section.ReserveBytes((int)countVal.Value, fill);
                }
                break;
        }
    }

    /// <summary>
    /// Parses name and section type out of a SectionDirective node. Returns false and
    /// reports a diagnostic if the name token is absent (malformed input).
    /// </summary>
    private bool TryParseSectionHeader(SyntaxNode node,
        out string? name, out SectionType sectionType)
    {
        name = null;
        sectionType = SectionType.Rom0;

        foreach (var t in node.ChildTokens())
        {
            if (t.Kind == SyntaxKind.StringLiteral)
                name = t.Text.Length >= 2 ? t.Text[1..^1] : t.Text;

            sectionType = t.Kind switch
            {
                SyntaxKind.Rom0Keyword  => SectionType.Rom0,
                SyntaxKind.RomxKeyword  => SectionType.RomX,
                SyntaxKind.Wram0Keyword => SectionType.Wram0,
                SyntaxKind.WramxKeyword => SectionType.WramX,
                SyntaxKind.VramKeyword  => SectionType.Vram,
                SyntaxKind.HramKeyword  => SectionType.Hram,
                SyntaxKind.SramKeyword  => SectionType.Sram,
                SyntaxKind.OamKeyword   => SectionType.Oam,
                _ => sectionType,
            };
        }

        if (name != null) return true;

        _diagnostics.Report(node.FullSpan, "SECTION directive requires a name");
        return false;
    }

    private void Pass2Instruction(SyntaxNode node)
    {
        var section = _sections.ActiveSection;
        if (section == null)
        {
            _diagnostics.Report(node.FullSpan, "Instruction outside of a section");
            return;
        }

        var desc = MatchInstruction(node);
        if (desc == null)
        {
            var mnemonic = node.ChildTokens().First().Text;
            _diagnostics.Report(node.FullSpan, $"No valid encoding for '{mnemonic}' with given operands");
            return;
        }

        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => section.CurrentPC);

        // Record the offset of the first opcode byte before emitting (needed for OpcodeOrImm8).
        int opcodeOffset = section.CurrentOffset;

        // Emit opcode bytes
        foreach (var b in desc.Encoding)
            section.EmitByte(b);

        // Emit operand data bytes per emit rules
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
                            section.EmitByte(0x00); // placeholder
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
                                // CurrentPC already includes the reserved byte (+1 from ReserveByte),
                                // so this equals instrBase + 2 — the correct post-instruction PC.
                                PCAfterInstruction = section.CurrentPC,
                            });
                    }
                    break;

                case EmitRuleKind.OpcodeOrImm8:
                    // OR the operand value into the first opcode byte already emitted.
                    // Used by RST: RST $08 → 0xC7 | 0x08 = 0xCF.
                    if (value.HasValue)
                        section.ApplyPatch(opcodeOffset, (byte)(desc.Encoding[0] | (value.Value & 0xFF)));
                    else
                        _diagnostics.Report(node.FullSpan,
                            "RST vector must be a constant expression");
                    break;
            }
        }
    }

    /// <summary>
    /// Match an instruction node against the SM83 table.
    /// </summary>
    private InstructionDescriptor? MatchInstruction(SyntaxNode node)
    {
        var green = node.Green;
        var greenNode = (GreenNode)green;

        // First child is the mnemonic token
        var mnemonicToken = (GreenToken)greenNode.GetChild(0)!;
        var mnemonic = mnemonicToken.Text;

        // Collect operand nodes (skip mnemonic and commas)
        var operandGreens = new List<GreenNodeBase>();
        for (int i = 1; i < greenNode.ChildCount; i++)
        {
            var child = greenNode.GetChild(i)!;
            if (child is GreenToken t && t.Kind == SyntaxKind.CommaToken)
                continue;
            if (child is GreenNode n && IsOperandKind(n.Kind))
                operandGreens.Add(n);
        }

        // Convert to patterns
        var patterns = operandGreens.Select(OperandPatternMatcher.PatternOf).ToList();

        // Evaluate operand values for pattern matching (range checks)
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0);
        var values = operandGreens.Select(op =>
        {
            var expr = GetOperandExpressionFromGreen(op);
            return expr != null ? evaluator.TryEvaluate(expr) : null;
        }).ToList();

        return OperandPatternMatcher.Match(mnemonic, patterns, values);
    }

    /// <summary>
    /// Get the expression node inside an operand (for evaluation).
    /// </summary>
    private static GreenNodeBase? GetOperandExpression(SyntaxNode instrNode, int operandIndex)
    {
        var greenNode = (GreenNode)instrNode.Green;
        int opIdx = 0;
        for (int i = 1; i < greenNode.ChildCount; i++)
        {
            var child = greenNode.GetChild(i)!;
            if (child is GreenToken t && t.Kind == SyntaxKind.CommaToken)
                continue;
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
            SyntaxKind.ImmediateOperand => green.GetChild(0), // expression inside
            SyntaxKind.LabelOperand     => green.GetChild(0), // label token → evaluate as name
            // Indirect operand: extract the expression inside the brackets so that
            // AppendImm16LE / AppendImm8 can emit the address. The inner expression
            // is the first non-bracket child (index 1 — after the '[' token).
            SyntaxKind.IndirectOperand  => GetIndirectExpression(green),
            _ => null, // registers/conditions don't have evaluable expressions
        };
    }

    /// <summary>
    /// Returns the inner expression node from an IndirectOperand green node if it carries
    /// an evaluable immediate address (e.g. <c>[HL]</c> → null, <c>[$C000]</c> → expression).
    /// Register-based and post-increment/decrement indirects have no evaluable address byte.
    /// </summary>
    private static GreenNodeBase? GetIndirectExpression(GreenNode indirect)
    {
        // Children: '[', content..., ']'
        // content is either:
        //   - a register token  ([BC], [DE], [HL], [C]) — no evaluable address
        //   - flat HL + +/- tokens ([HL+], [HL-]) — no evaluable address
        //   - an expression node ([$C000], [$FF], label) — evaluable
        if (indirect.ChildCount < 3) return null;
        var inner = indirect.GetChild(1);

        if (inner is not GreenNode exprNode) return null; // flat token — register, not address

        // A NameExpression wrapping a register keyword is still a register reference,
        // not a numeric address. Discard it to avoid spurious "undefined symbol" diagnostics.
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

    private static GreenNodeBase GetGreenNode(SyntaxNode node) => node.Green;
}
