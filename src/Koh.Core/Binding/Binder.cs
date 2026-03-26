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
    private readonly InstructionEncoder _encoder;

    public Binder()
    {
        _symbols = new SymbolTable(_diagnostics);
        _encoder = new InstructionEncoder(_symbols, _diagnostics);
    }

    public BindingResult Bind(SyntaxTree tree)
    {
        foreach (var d in tree.Diagnostics)
            _diagnostics.Report(d.Span, d.Message, d.Severity);

        // Pre-pass: expand macros, REPT/FOR, IF/ELIF/ELSE/ENDC into flat list
        var expander = new AssemblyExpander(_diagnostics, _symbols);
        var nodes = expander.Expand(tree);

        // Pass 1: symbol collection and PC tracking
        var pcTracker = new SectionPCTracker();
        foreach (var en in nodes)
            Pass1Node(en.Node, pcTracker);

        // Pass 2: byte emission
        foreach (var en in nodes)
            Pass2Node(en.Node);

        new PatchResolver(_symbols, _sections, _diagnostics).ApplyAll();

        foreach (var sym in _symbols.GetUndefinedSymbols())
            _diagnostics.Report(default, $"Undefined symbol '{sym.Name}'");

        return new BindingResult(_sections.AllSections, _symbols, _diagnostics.ToList());
    }

    public EmitModel BindToEmitModel(SyntaxTree tree) =>
        EmitModel.FromBindingResult(Bind(tree));

    // =========================================================================
    // Pass 1 — symbol collection and PC tracking (flat iteration, no blocks)
    // =========================================================================

    private void Pass1Node(SyntaxNode node, SectionPCTracker pc)
    {
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
                // EQU constants are defined by the AssemblyExpander before Pass 1 runs;
                // do not redefine them here. Only EXPORT visibility marking belongs in Pass 1.
                Pass1Export(node);
                break;
        }
    }

    private void Pass1Label(SyntaxNode node, SectionPCTracker pc)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count == 0) return;
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

    /// <summary>
    /// Pass 1 only needs to process EXPORT visibility from SymbolDirective nodes.
    /// EQU and REDEF constants are fully resolved by AssemblyExpander before Pass 1 runs;
    /// re-evaluating them here would cause duplicate-definition diagnostics.
    /// </summary>
    private void Pass1Export(SyntaxNode node)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count < 2) return;

        if (tokens[0].Kind == SyntaxKind.ExportKeyword)
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
    // Pass 2 — byte emission (flat iteration, no blocks)
    // =========================================================================

    private void Pass2Node(SyntaxNode node)
    {
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
            case SyntaxKind.SymbolDirective:
                Pass2Symbol(node);
                break;
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

    private void Pass2Symbol(SyntaxNode node)
    {
        // EQU/REDEF constants are fully resolved by AssemblyExpander before any pass.
        // EXPORT visibility is marked in Pass1Export.
        // Nothing to emit in Pass 2 for symbol directives.
    }
}
