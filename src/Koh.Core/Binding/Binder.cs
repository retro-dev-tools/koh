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

        // Check for undefined symbols
        foreach (var sym in _symbols.GetUndefinedSymbols())
            _diagnostics.Report(default, $"Undefined symbol '{sym.Name}'");

        // Pass 2: emit bytes (Task 5.2b will implement instruction encoding)
        Pass2(tree.Root);

        return new BindingResult(
            _sections.AllSections,
            _symbols,
            _diagnostics.ToList());
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
        var nameToken = node.ChildTokens().First();
        _symbols.DefineLabel(nameToken.Text, pc.CurrentPC, pc.ActiveSectionName, node);
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
        // For now, estimate instruction size from operand count
        // Task 5.2b will use the InstructionTable for exact sizing
        var mnemonic = node.ChildTokens().First();
        var operandNodes = node.ChildNodes().ToList();

        int size = EstimateInstructionSize(mnemonic.Kind, operandNodes.Count);
        pc.Advance(size);
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
        // EXPORT, PURGE — no PC impact, handled by binder later
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
                    // Task 5.2b will implement instruction encoding
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
    /// Estimate instruction size for Pass 1. Will be replaced by InstructionTable in Task 5.2b.
    /// </summary>
    private static int EstimateInstructionSize(SyntaxKind mnemonic, int operandCount)
    {
        // Zero-operand instructions (NOP, HALT, DI, EI, etc.) = 1 byte
        // One-operand instructions vary: RST = 1, JR = 2, JP/CALL = 3, PUSH/POP = 1
        // Two-operand instructions vary: LD r,r = 1, LD r,n = 2, LD rr,nn = 3
        // CB-prefix instructions = 2 bytes
        // This is a rough estimate — Task 5.2b replaces this with exact table lookups
        return operandCount switch
        {
            0 => 1,
            1 => mnemonic switch
            {
                SyntaxKind.JpKeyword or SyntaxKind.CallKeyword => 3,
                SyntaxKind.JrKeyword => 2,
                SyntaxKind.RstKeyword => 1,
                _ => 1,
            },
            2 => mnemonic switch
            {
                SyntaxKind.LdKeyword => 1, // conservative for LD r,r; will be fixed by table
                _ => 2,
            },
            _ => 1,
        };
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

    private static GreenNodeBase GetGreenNode(SyntaxNode node) => node.Green;
}
