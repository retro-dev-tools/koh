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
    private readonly ISourceFileResolver _fileResolver;
    private readonly CharMapManager _charMaps;

    public Binder(ISourceFileResolver? fileResolver = null)
    {
        _symbols = new SymbolTable(_diagnostics);
        _encoder = new InstructionEncoder(_symbols, _diagnostics);
        _fileResolver = fileResolver ?? new FileSystemResolver();
        _charMaps = new CharMapManager(_diagnostics);
    }

    public BindingResult Bind(SyntaxTree tree)
    {
        foreach (var d in tree.Diagnostics)
            _diagnostics.Report(d.Span, d.Message, d.Severity);

        // Pre-pass: expand macros, REPT/FOR, IF/ELIF/ELSE/ENDC, INCLUDE into flat list
        var expander = new AssemblyExpander(_diagnostics, _symbols, _fileResolver);
        var nodes = expander.Expand(tree);

        // Pass 1: symbol collection and PC tracking
        var pcTracker = new SectionPCTracker();
        foreach (var en in nodes)
            Pass1Node(en, pcTracker);

        // Pass 2: byte emission
        foreach (var en in nodes)
            Pass2Node(en);

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

    private void Pass1Node(ExpandedNode en, SectionPCTracker pc)
    {
        var node = en.Node;
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
            case SyntaxKind.IncludeDirective:
                Pass1Incbin(node, en.SourceFilePath, pc);
                break;
            case SyntaxKind.SymbolDirective:
                // EQU constants are defined by the AssemblyExpander before Pass 1 runs;
                // do not redefine them here. Only EXPORT visibility marking belongs in Pass 1.
                // Charmap directives are also processed here so Pass1Data can compute
                // accurate byte counts for strings using multi-char charmap mappings.
                Pass1Export(node);
                Pass1Charmap(node);
                break;
            case SyntaxKind.DirectiveStatement:
                Pass1Directive(node, pc);
                break;
        }
    }

    private void Pass1Directive(SyntaxNode node, SectionPCTracker pc)
    {
        var kw = node.ChildTokens().FirstOrDefault();
        if (kw == null) return;

        switch (kw.Kind)
        {
            case SyntaxKind.PushsKeyword:
                pc.PushSection();
                break;
            case SyntaxKind.PopsKeyword:
                // Diagnostic for unmatched POPS is reported by Pass2Directive only,
                // to avoid duplicate error messages.
                pc.PopSection();
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
            {
                int byteCount = 0;
                foreach (var expr in expressions)
                {
                    if (IsStringLiteral(expr.Green))
                        byteCount += _charMaps.EncodeString(ExtractStringText(expr.Green)).Length;
                    else
                        byteCount++;
                }
                pc.Advance(byteCount);
                break;
            }
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

    private void Pass1Incbin(SyntaxNode node, string sourceFilePath, SectionPCTracker pc)
    {
        var kw = node.ChildTokens().FirstOrDefault();
        if (kw?.Kind != SyntaxKind.IncbinKeyword) return;

        var strToken = node.ChildTokens().FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
        if (strToken == null) return;

        var filePath = strToken.Text.Length >= 2 ? strToken.Text[1..^1] : strToken.Text;
        var resolved = _fileResolver.ResolvePath(sourceFilePath, filePath);

        if (_fileResolver.FileExists(resolved))
        {
            try
            {
                var bytes = _fileResolver.ReadAllBytes(resolved);
                pc.Advance(bytes.Length);
            }
            catch (IOException ex)
            {
                _diagnostics.Report(node.FullSpan,
                    $"Cannot read INCBIN file '{filePath}': {ex.Message}");
                // PC not advanced — consistent with Pass 2 which won't emit bytes either
            }
        }
    }

    /// <summary>
    /// Pass 1 only needs to process EXPORT visibility from SymbolDirective nodes.
    /// EQU and REDEF constants are fully resolved by AssemblyExpander before Pass 1 runs;
    /// re-evaluating them here would cause duplicate-definition diagnostics.
    /// </summary>
    private void Pass1Charmap(SyntaxNode node)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count == 0) return;

        switch (tokens[0].Kind)
        {
            case SyntaxKind.NewcharmapKeyword:
                if (tokens.Count >= 2)
                    _charMaps.NewCharMap(StripQuotes(tokens[1].Text));
                break;
            case SyntaxKind.SetcharmapKeyword:
                if (tokens.Count >= 2)
                    _charMaps.SetCharMap(StripQuotes(tokens[1].Text));
                break;
            case SyntaxKind.PrecharmapKeyword:
                _charMaps.PushCharMap();
                break;
            case SyntaxKind.PopcharmapKeyword:
                _charMaps.PopCharMap();
                break;
            case SyntaxKind.CharmapKeyword:
                if (tokens.Count >= 2)
                {
                    var charStr = tokens[1].Text;
                    if (charStr.Length >= 2) charStr = charStr[1..^1];
                    for (int i = tokens.Count - 1; i >= 2; i--)
                    {
                        if (tokens[i].Kind == SyntaxKind.NumberLiteral)
                        {
                            var val = ExpressionEvaluator.ParseNumber(tokens[i].Text);
                            if (val.HasValue)
                                _charMaps.AddMapping(charStr, (byte)(val.Value & 0xFF));
                            break;
                        }
                    }
                }
                break;
        }
    }

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

    private void Pass2Node(ExpandedNode en)
    {
        var node = en.Node;
        switch (node.Kind)
        {
            case SyntaxKind.SectionDirective:
                Pass2Section(node);
                break;
            case SyntaxKind.DataDirective:
                Pass2Data(node);
                break;
            case SyntaxKind.IncludeDirective:
                Pass2Incbin(node, en.SourceFilePath);
                break;
            case SyntaxKind.InstructionStatement:
                Pass2Instruction(node);
                break;
            // SymbolDirective: charmap directives handled in Pass1Charmap,
            // EQU/EXPORT handled in Pass1. Nothing to do in Pass2.
            case SyntaxKind.DirectiveStatement:
                Pass2Directive(node);
                break;
        }
    }

    private void Pass2Incbin(SyntaxNode node, string sourceFilePath)
    {
        var kw = node.ChildTokens().FirstOrDefault();
        if (kw?.Kind != SyntaxKind.IncbinKeyword) return;

        var section = _sections.ActiveSection;
        if (section == null)
        {
            _diagnostics.Report(node.FullSpan, "INCBIN outside of a section");
            return;
        }

        var strToken = node.ChildTokens().FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
        if (strToken == null)
        {
            _diagnostics.Report(node.FullSpan, "INCBIN requires a filename string");
            return;
        }

        var filePath = strToken.Text.Length >= 2 ? strToken.Text[1..^1] : strToken.Text;
        var resolved = _fileResolver.ResolvePath(sourceFilePath, filePath);

        if (!_fileResolver.FileExists(resolved))
        {
            _diagnostics.Report(node.FullSpan, $"INCBIN file not found: {filePath}");
            return;
        }

        try
        {
            var bytes = _fileResolver.ReadAllBytes(resolved);
            foreach (var b in bytes)
                section.EmitByte(b);
        }
        catch (IOException ex)
        {
            _diagnostics.Report(node.FullSpan, $"Cannot read INCBIN file '{filePath}': {ex.Message}");
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
                    // String literals in DB: encode through character map
                    if (IsStringLiteral(expr.Green))
                    {
                        var text = ExtractStringText(expr.Green);
                        foreach (var b in _charMaps.EncodeString(text))
                            section.EmitByte(b);
                        continue;
                    }

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

    private static string StripQuotes(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    private void Pass2Directive(SyntaxNode node)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count == 0) return;

        switch (tokens[0].Kind)
        {
            case SyntaxKind.AssertKeyword:
            case SyntaxKind.StaticAssertKeyword:
            {
                var exprNodes = node.ChildNodes().ToList();
                if (exprNodes.Count == 0)
                {
                    _diagnostics.Report(node.FullSpan, "ASSERT requires a condition expression");
                    return;
                }
                var section = _sections.ActiveSection;
                var evaluator = new ExpressionEvaluator(_symbols, _diagnostics,
                    () => section?.CurrentPC ?? 0);
                var val = evaluator.TryEvaluate(exprNodes[0].Green);

                // Determine severity: ASSERT WARN, ... → warning; ASSERT FAIL/FATAL, ... → error (default)
                var severity = DiagnosticSeverity.Error;
                for (int ti = 1; ti < tokens.Count; ti++)
                {
                    if (tokens[ti].Kind == SyntaxKind.WarnKeyword) { severity = DiagnosticSeverity.Warning; break; }
                    if (tokens[ti].Kind is SyntaxKind.FailKeyword or SyntaxKind.FatalKeyword) break;
                    if (tokens[ti].Kind == SyntaxKind.CommaToken) break; // past severity position
                }

                string GetAssertMessage()
                {
                    for (int ti = 0; ti < tokens.Count; ti++)
                        if (tokens[ti].Kind == SyntaxKind.StringLiteral && tokens[ti].Text.Length >= 2)
                            return tokens[ti].Text[1..^1];
                    return "Assertion failed";
                }

                if (val.HasValue && val.Value == 0)
                {
                    _diagnostics.Report(node.FullSpan, GetAssertMessage(), severity);
                }
                else if (!val.HasValue && tokens[0].Kind == SyntaxKind.StaticAssertKeyword)
                {
                    _diagnostics.Report(node.FullSpan,
                        "STATIC_ASSERT condition could not be evaluated at assembly time");
                }
                // Regular ASSERT with null: deferred to link time (not yet implemented)
                break;
            }

            case SyntaxKind.WarnKeyword:
            {
                var msgToken = tokens.FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
                var msg = msgToken != null && msgToken.Text.Length >= 2
                    ? msgToken.Text[1..^1]
                    : "WARN directive";
                _diagnostics.Report(node.FullSpan, msg, DiagnosticSeverity.Warning);
                break;
            }

            case SyntaxKind.FailKeyword:
            {
                var msgToken = tokens.FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
                var msg = msgToken != null && msgToken.Text.Length >= 2
                    ? msgToken.Text[1..^1]
                    : "FAIL directive";
                _diagnostics.Report(node.FullSpan, msg);
                break;
            }

            case SyntaxKind.PrintKeyword:
            case SyntaxKind.PrintlnKeyword:
                // PRINT/PRINTLN — no-op during binding (compile-time output only)
                break;

            case SyntaxKind.PushsKeyword:
                _sections.PushSection();
                break;

            case SyntaxKind.PopsKeyword:
                if (!_sections.PopSection())
                    _diagnostics.Report(node.FullSpan, "POPS without matching PUSHS");
                break;
        }
    }

    private static bool IsStringLiteral(Syntax.InternalSyntax.GreenNodeBase green)
    {
        if (green is Syntax.InternalSyntax.GreenNode node &&
            node.Kind == SyntaxKind.LiteralExpression)
        {
            var child = node.GetChild(0);
            return child is Syntax.InternalSyntax.GreenToken t &&
                   t.Kind == SyntaxKind.StringLiteral;
        }
        return false;
    }

    private static string ExtractStringText(Syntax.InternalSyntax.GreenNodeBase green)
    {
        var node = (Syntax.InternalSyntax.GreenNode)green;
        var token = (Syntax.InternalSyntax.GreenToken)node.GetChild(0)!;
        var text = token.Text;
        return text.Length >= 2 ? text[1..^1] : text; // strip quotes
    }
}
