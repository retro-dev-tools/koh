using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Binding;

public sealed class Binder
{
    private readonly DiagnosticBag _diagnostics = new();
    private readonly SymbolTable _symbols;
    private readonly SectionManager _sections = new();
    private readonly ConditionalAssemblyState _conditional = new();
    private readonly InstructionEncoder _encoder;
    private readonly MacroExpander _macros;
    private bool _insideMacroDefinition; // skip body nodes between MACRO and ENDM
    // TODO: _repeatSkipDepth and _sourceText will be removed by AssemblyExpander refactor

    public Binder()
    {
        _symbols = new SymbolTable(_diagnostics);
        _encoder = new InstructionEncoder(_symbols, _diagnostics);
        _macros = new MacroExpander(_diagnostics);
    }

    public BindingResult Bind(SyntaxTree tree)
    {
        foreach (var d in tree.Diagnostics)
            _diagnostics.Report(d.Span, d.Message, d.Severity);

        // Collect macro definitions before passes
        _macros.CollectDefinitions(tree.Root, tree.Text);

        var pcTracker = new SectionPCTracker();
        Pass1(tree.Root, pcTracker);

        if (_conditional.HasUnclosedBlocks)
            _diagnostics.Report(default, "Unclosed IF block: missing ENDC");

        Pass2(tree.Root);

        new PatchResolver(_symbols, _sections, _diagnostics).ApplyAll();

        foreach (var sym in _symbols.GetUndefinedSymbols())
            _diagnostics.Report(default, $"Undefined symbol '{sym.Name}'");

        return new BindingResult(_sections.AllSections, _symbols, _diagnostics.ToList());
    }

    public EmitModel BindToEmitModel(SyntaxTree tree) =>
        EmitModel.FromBindingResult(Bind(tree));

    // =========================================================================
    // Pass 1 — symbol collection and PC tracking
    // =========================================================================

    private void Pass1(SyntaxNode root, SectionPCTracker pc)
    {
        foreach (var child in root.ChildNodesAndTokens())
        {
            if (!child.IsNode) continue;
            var node = child.AsNode!;

            if (node.Kind == SyntaxKind.ConditionalDirective)
            {
                HandleConditional(node, pc);
                continue;
            }

            if (_conditional.IsSuppressed) continue;

            // Skip macro body nodes (between MACRO and ENDM)
            if (_insideMacroDefinition && node.Kind != SyntaxKind.MacroDefinition)
                continue;

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
                case SyntaxKind.MacroDefinition:
                    ToggleMacroSkip(node);
                    break;
                case SyntaxKind.MacroCall:
                    ExpandMacroCall(node, pc, pass: 1);
                    break;
                case SyntaxKind.RepeatDirective:
                    break; // TODO: REPT expansion — will be handled by AssemblyExpander refactor
            }
        }
    }

    private void Pass1Label(SyntaxNode node, SectionPCTracker pc)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count == 0) return; // malformed node — parser invariant violated
        var sym = _symbols.DefineLabel(tokens[0].Text, pc.CurrentPC, pc.ActiveSectionName, node);
        if (tokens.Count >= 2 && tokens[1].Kind == SyntaxKind.DoubleColonToken)
            sym.Visibility = SymbolVisibility.Exported;
    }

    private void Pass1Section(SyntaxNode node, SectionPCTracker pc)
    {
        if (!SectionHeaderParser.TryParse(node, _diagnostics,
                out var name, out _, out var fixedAddress, out _))
            return;
        pc.SetActive(name!, fixedAddress ?? 0);
    }

    private void Pass1Instruction(SyntaxNode node, SectionPCTracker pc)
    {
        var desc = _encoder.Match(node);
        pc.Advance(desc?.Size ?? 1);
    }

    private void Pass1Data(SyntaxNode node, SectionPCTracker pc)
    {
        var keyword = node.ChildTokens().First();
        var expressions = node.ChildNodes().ToList();

        switch (keyword.Kind)
        {
            case SyntaxKind.DbKeyword:
                pc.Advance(expressions.Count);
                break;
            case SyntaxKind.DwKeyword:
                pc.Advance(expressions.Count * 2);
                break;
            case SyntaxKind.DsKeyword:
                if (expressions.Count > 0)
                {
                    var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => pc.CurrentPC);
                    var sizeVal = evaluator.TryEvaluate(expressions[0].Green);
                    pc.Advance(sizeVal.HasValue ? (int)sizeVal.Value : 0);
                }
                break;
        }
    }

    private void Pass1Symbol(SyntaxNode node, SectionPCTracker pc)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count < 2) return;

        var first = tokens[0];

        if (first.Kind == SyntaxKind.IdentifierToken &&
            tokens.Count >= 2 && tokens[1].Kind == SyntaxKind.EquKeyword)
        {
            var exprNodes = node.ChildNodes().ToList();
            if (exprNodes.Count > 0)
            {
                var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => pc.CurrentPC);
                var value = evaluator.TryEvaluate(exprNodes[0].Green);
                if (value.HasValue)
                    _symbols.DefineConstant(first.Text, value.Value, node);
            }
        }
        else if (first.Kind == SyntaxKind.ExportKeyword)
        {
            for (int i = 1; i < tokens.Count; i++)
                if (tokens[i].Kind == SyntaxKind.IdentifierToken)
                {
                    var sym = _symbols.Lookup(tokens[i].Text)
                              ?? _symbols.DeclareForwardRef(tokens[i].Text);
                    sym.Visibility = SymbolVisibility.Exported;
                }
        }
    }

    // =========================================================================
    // Pass 2 — byte emission
    // =========================================================================

    private void Pass2(SyntaxNode root)
    {
        _conditional.Reset();
        _insideMacroDefinition = false;

        foreach (var child in root.ChildNodesAndTokens())
        {
            if (!child.IsNode) continue;
            var node = child.AsNode!;

            if (node.Kind == SyntaxKind.ConditionalDirective)
            {
                HandleConditional(node, null);
                continue;
            }

            if (_conditional.IsSuppressed) continue;
            if (_insideMacroDefinition && node.Kind != SyntaxKind.MacroDefinition)
                continue;

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
                case SyntaxKind.MacroDefinition:
                    ToggleMacroSkip(node);
                    break;
                case SyntaxKind.MacroCall:
                    ExpandMacroCall(node, null, pass: 2);
                    break;
            }
        }
    }

    private void Pass2Section(SyntaxNode node)
    {
        if (!SectionHeaderParser.TryParse(node, _diagnostics,
                out var name, out var sectionType, out var fixedAddress, out var bank))
            return;
        _sections.OpenOrResume(name!, sectionType, fixedAddress, bank);
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
                    var val = evaluator.TryEvaluate(expr.Green);
                    if (val.HasValue)
                        section.EmitByte((byte)(val.Value & 0xFF));
                    else
                    {
                        int offset = section.ReserveByte();
                        section.RecordPatch(new PatchEntry
                        {
                            SectionName = section.Name, Offset = offset,
                            Expression = expr.Green, Kind = PatchKind.Absolute8,
                        });
                    }
                }
                break;

            case SyntaxKind.DwKeyword:
                foreach (var expr in expressions)
                {
                    var val = evaluator.TryEvaluate(expr.Green);
                    if (val.HasValue)
                        section.EmitWord((ushort)(val.Value & 0xFFFF));
                    else
                    {
                        int offset = section.ReserveWord();
                        section.RecordPatch(new PatchEntry
                        {
                            SectionName = section.Name, Offset = offset,
                            Expression = expr.Green, Kind = PatchKind.Absolute16,
                        });
                    }
                }
                break;

            case SyntaxKind.DsKeyword:
                if (expressions.Count > 0)
                {
                    var countVal = evaluator.TryEvaluate(expressions[0].Green);
                    byte fill = 0x00;
                    if (expressions.Count > 1)
                    {
                        var fillVal = evaluator.TryEvaluate(expressions[1].Green);
                        if (fillVal.HasValue) fill = (byte)(fillVal.Value & 0xFF);
                    }
                    if (countVal.HasValue)
                        section.ReserveBytes((int)countVal.Value, fill);
                }
                break;
        }
    }

    private void ToggleMacroSkip(SyntaxNode node)
    {
        var kw = node.ChildTokens().FirstOrDefault();
        if (kw?.Kind == SyntaxKind.MacroKeyword)
            _insideMacroDefinition = true;
        else if (kw?.Kind == SyntaxKind.EndmKeyword)
            _insideMacroDefinition = false;
    }

    private void ExpandMacroCall(SyntaxNode node, SectionPCTracker? pc, int pass)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count == 0) return;

        var name = tokens[0].Text;
        if (!_macros.IsMacro(name))
        {
            if (pass == 2) // only report in Pass 2 to avoid duplicate diagnostics
                _diagnostics.Report(node.FullSpan, $"Unexpected identifier '{name}'");
            return;
        }

        // Collect comma-separated arguments as raw text
        var args = new List<string>();
        var currentArg = new List<string>();
        for (int i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == SyntaxKind.CommaToken)
            {
                args.Add(string.Join(" ", currentArg));
                currentArg.Clear();
            }
            else
            {
                currentArg.Add(tokens[i].Text);
            }
        }
        if (currentArg.Count > 0)
            args.Add(string.Join(" ", currentArg));

        var expanded = _macros.Expand(name, args);
        if (expanded == null) return;

        // Walk the expanded tree through the current pass
        if (pass == 1 && pc != null)
            Pass1(expanded.Root, pc);
        else if (pass == 2)
            Pass2(expanded.Root);
    }

    private void Pass2Instruction(SyntaxNode node)
    {
        var section = _sections.ActiveSection;
        if (section == null)
        {
            _diagnostics.Report(node.FullSpan, "Instruction outside of a section");
            return;
        }

        var desc = _encoder.Match(node);
        if (desc == null)
        {
            var mnemonic = node.ChildTokens().First().Text;
            _diagnostics.Report(node.FullSpan,
                $"No valid encoding for '{mnemonic}' with given operands");
            return;
        }

        _encoder.Encode(node, desc, section);
    }

    // =========================================================================
    // Conditional assembly
    // =========================================================================

    private void HandleConditional(SyntaxNode node, SectionPCTracker? pc)
    {
        var keyword = ((GreenToken)((GreenNode)node.Green).GetChild(0)!).Kind;

        // Lazy evaluator — only called when the state machine needs the condition value
        bool Eval() => EvaluateCondition(node, pc);

        switch (keyword)
        {
            case SyntaxKind.IfKeyword:
                _conditional.HandleIf(Eval);
                break;
            case SyntaxKind.ElifKeyword:
                if (!_conditional.HandleElif(Eval))
                    _diagnostics.Report(node.FullSpan, "ELIF without matching IF");
                break;
            case SyntaxKind.ElseKeyword:
                if (!_conditional.HandleElse())
                    _diagnostics.Report(node.FullSpan, "ELSE without matching IF");
                break;
            case SyntaxKind.EndcKeyword:
                if (!_conditional.HandleEndc())
                    _diagnostics.Report(node.FullSpan, "ENDC without matching IF");
                break;
        }
    }

    private bool EvaluateCondition(SyntaxNode node, SectionPCTracker? pc)
    {
        var exprNodes = node.ChildNodes().ToList();
        if (exprNodes.Count == 0) return false;
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics,
            () => pc?.CurrentPC ?? 0);
        var value = evaluator.TryEvaluate(exprNodes[0].Green);
        return value.HasValue && value.Value != 0;
    }
}
