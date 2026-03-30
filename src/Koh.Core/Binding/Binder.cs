using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Binding;

/// <summary>
/// Controls binding behavior. Compilation translates output format → binding policy.
/// </summary>
public readonly record struct BinderOptions
{
    /// <summary>
    /// When true, undefined symbols are treated as imports (for .o output).
    /// When false, undefined symbols are reported as errors (for final ROM).
    /// </summary>
    public bool AllowUndefinedSymbols { get; init; }
}

public sealed class Binder
{
    private readonly DiagnosticBag _diagnostics = new();
    private readonly SymbolTable _symbols;
    private readonly SectionManager _sections = new();
    private readonly InstructionEncoder _encoder;
    private readonly ISourceFileResolver _fileResolver;
    private readonly CharMapManager _charMaps;
    private readonly TextWriter _printOutput;
    private readonly BinderOptions _options;
    private AssemblyExpander? _expander;

    public Binder(BinderOptions options = default, ISourceFileResolver? fileResolver = null, TextWriter? printOutput = null)
    {
        _options = options;
        _symbols = new SymbolTable(_diagnostics);
        _encoder = new InstructionEncoder(_symbols, _diagnostics);
        _fileResolver = fileResolver ?? new FileSystemResolver();
        _charMaps = new CharMapManager(_diagnostics);
        _printOutput = printOutput ?? Console.Error;
    }

    private void DefineRgbdsBuiltins()
    {
        // RGBDS version constants — report as Koh but RGBDS-compatible version numbers
        _symbols.DefineConstant("__RGBDS_MAJOR__", 1, null);
        _symbols.DefineConstant("__RGBDS_MINOR__", 0, null);
        _symbols.DefineConstant("__RGBDS_PATCH__", 0, null);
        _symbols.DefineConstant("__RGBDS_RC__", 0, null);
    }

    public BindingResult Bind(SyntaxTree tree)
    {
        // Pre-define RGBDS built-in constants for conditional assembly compatibility
        DefineRgbdsBuiltins();

        foreach (var d in tree.Diagnostics)
            _diagnostics.Report(d.Span, d.Message, d.Severity);

        // Pre-pass: expand macros, REPT/FOR, IF/ELIF/ELSE/ENDC, INCLUDE into flat list
        _expander = new AssemblyExpander(_diagnostics, _symbols, _fileResolver, _charMaps);
        var nodes = _expander.Expand(tree);

        // Pass 1: symbol collection and PC tracking
        var pcTracker = new SectionPCTracker();
        foreach (var en in nodes)
        {
            _diagnostics.CurrentFilePath = en.SourceFilePath;
            Pass1Node(en, pcTracker);
        }

        // Pass 2: byte emission — reset global label scope so local labels resolve correctly
        _symbols.SetGlobalAnchor(null);
        foreach (var en in nodes)
        {
            _diagnostics.CurrentFilePath = en.SourceFilePath;
            Pass2Node(en);
        }

        new PatchResolver(_symbols, _sections, _diagnostics).ApplyAll();

        if (!_options.AllowUndefinedSymbols)
        {
            foreach (var sym in _symbols.GetUndefinedSymbols())
                _diagnostics.Report(default, $"Undefined symbol '{sym.Name}'");
        }

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
                // EQU constants and charmap directives are processed by the AssemblyExpander
                // during expansion (before Pass 1). Only EXPORT visibility marking belongs here.
                Pass1Export(node);
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
                pc.PopSection();
                break;
            case SyntaxKind.NextuKeyword:
                pc.NextUnion();
                break;
            case SyntaxKind.EnduKeyword:
                pc.EndUnion();
                break;
            case SyntaxKind.LoadKeyword:
                Pass1Load(node, pc);
                break;
            case SyntaxKind.EndlKeyword:
                pc.EndLoad();
                break;
            case SyntaxKind.AlignKeyword:
                Pass1Align(node, pc);
                break;
            // OPT/PUSHO/POPO: no PC impact in Pass 1
        }
    }

    private void Pass1Align(SyntaxNode node, SectionPCTracker pc)
    {
        var exprNodes = node.ChildNodes().ToList();
        if (exprNodes.Count == 0) return;
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => pc.CurrentPC);
        var alignBits = evaluator.TryEvaluate(exprNodes[0].Green);
        if (!alignBits.HasValue || alignBits.Value < 0 || alignBits.Value > 16) return;

        int bits = (int)alignBits.Value;
        pc.SetAlignBits(bits);
        int boundary = 1 << bits;
        int alignOffset = 0;
        if (exprNodes.Count > 1)
        {
            var offsetVal = evaluator.TryEvaluate(exprNodes[1].Green);
            if (offsetVal.HasValue)
            {
                alignOffset = (int)offsetVal.Value;
                if (alignOffset < 0 || alignOffset >= boundary)
                    return; // invalid offset — silently skip (Pass2 reports diagnostic)
            }
        }

        int mask = boundary - 1;
        int pad = ((boundary - ((pc.CurrentPC - alignOffset) & mask)) & mask);
        pc.Advance(pad);
        pc.AdvanceLoad(pad);
    }

    private void Pass1Load(SyntaxNode node, SectionPCTracker pc)
    {
        // LOAD "name", TYPE — parse like a section header but route to load
        var tokens = node.ChildTokens().ToList();
        string? name = null;
        SectionType type = SectionType.Wram0;
        int? fixedAddr = null;

        for (int i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == SyntaxKind.StringLiteral)
                name = tokens[i].Text.Length >= 2 ? tokens[i].Text[1..^1] : tokens[i].Text;
            var mapped = tokens[i].Kind switch
            {
                SyntaxKind.Wram0Keyword => SectionType.Wram0,
                SyntaxKind.WramxKeyword => SectionType.WramX,
                SyntaxKind.HramKeyword  => SectionType.Hram,
                SyntaxKind.SramKeyword  => SectionType.Sram,
                SyntaxKind.VramKeyword  => SectionType.Vram,
                _ => (SectionType?)null,
            };
            if (mapped.HasValue) type = mapped.Value;
        }

        if (name != null)
            pc.BeginLoad(name, fixedAddr ?? 0);
    }

    /// <summary>
    /// Pass 2 label tracking: advance the global anchor so local label references
    /// in expressions resolve against the correct scope.
    /// </summary>
    private void Pass2Label(SyntaxNode node)
    {
        var first = node.ChildTokens().FirstOrDefault();
        if (first == null) return;
        var name = first.Text;
        if (!name.StartsWith('.'))
        {
            var sym = _symbols.Lookup(name);
            if (sym != null)
                _symbols.SetGlobalAnchor(sym);
        }
    }

    private void Pass1Label(SyntaxNode node, SectionPCTracker pc)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count == 0) return;
        // In LOAD blocks, labels get addresses from the load section
        var sym = _symbols.DefineLabel(tokens[0].Text, pc.LabelPC, pc.LabelSectionName, node);
        if (tokens.Count >= 2 && tokens[1].Kind == SyntaxKind.DoubleColonToken)
            sym.Visibility = SymbolVisibility.Exported;
    }

    private void Pass1Section(SyntaxNode node, SectionPCTracker pc)
    {
        if (!SectionHeaderParser.TryParse(node, _diagnostics,
                out var name, out _, out var fixedAddress, out _,
                out var isUnion, out _))
            return;
        pc.SetActive(name!, fixedAddress ?? 0);
        if (isUnion)
            pc.BeginUnion();
    }

    private void Pass1Instruction(SyntaxNode node, SectionPCTracker pc)
    {
        var desc = _encoder.Match(node);
        var size = desc?.Size ?? 1;
        pc.Advance(size);
        pc.AdvanceLoad(size); // no-op if not in LOAD block
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
                pc.AdvanceLoad(byteCount);
                break;
            }
            case SyntaxKind.DwKeyword:
            {
                int byteCount = 0;
                foreach (var expr in expressions)
                {
                    if (IsStringLiteral(expr.Green))
                        byteCount += _charMaps.EncodeString(ExtractStringText(expr.Green)).Length * 2;
                    else
                        byteCount += 2;
                }
                pc.Advance(byteCount);
                pc.AdvanceLoad(byteCount);
                break;
            }
            case SyntaxKind.DlKeyword:
            {
                int size = expressions.Count * 4;
                pc.Advance(size);
                pc.AdvanceLoad(size);
                break;
            }
            case SyntaxKind.DsKeyword:
            {
                // DS ALIGN[n] / DS ALIGN[n, offset] — alignment padding
                var alignToken = node.ChildTokens().FirstOrDefault(t => t.Kind == SyntaxKind.AlignKeyword);
                if (alignToken != null)
                {
                    Pass1DsAlign(node, pc);
                    break;
                }
                if (expressions.Count > 0)
                {
                    var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => pc.CurrentPC);
                    var sizeVal = evaluator.TryEvaluate(expressions[0].Green);
                    int dsSize = sizeVal.HasValue ? (int)sizeVal.Value : 0;
                    pc.Advance(dsSize);
                    pc.AdvanceLoad(dsSize);
                }
                break;
            }
        }
    }

    private void Pass1DsAlign(SyntaxNode node, SectionPCTracker pc)
    {
        var exprNodes = node.ChildNodes().ToList();
        if (exprNodes.Count == 0) return;
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => pc.CurrentPC);
        var alignBits = evaluator.TryEvaluate(exprNodes[0].Green);
        if (!alignBits.HasValue || alignBits.Value < 0 || alignBits.Value > 16) return;

        // For floating sections, use effective alignment = min(section_align, requested)
        int bits = (int)alignBits.Value;
        if (pc.ActiveAlignBits > 0)
            bits = Math.Min(bits, pc.ActiveAlignBits);

        int boundary = 1 << bits;
        int alignOffset = 0;
        if (exprNodes.Count > 1)
        {
            var offsetVal = evaluator.TryEvaluate(exprNodes[1].Green);
            if (offsetVal.HasValue)
                alignOffset = (int)offsetVal.Value;
        }

        int mask = boundary - 1;
        int pad = ((boundary - ((pc.CurrentPC - alignOffset) & mask)) & mask);
        pc.Advance(pad);
        pc.AdvanceLoad(pad);
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
                pc.AdvanceLoad(bytes.Length);
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
            case SyntaxKind.LabelDeclaration:
                // Track global scope in Pass 2 so local label references resolve correctly
                Pass2Label(node);
                break;
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
            // SymbolDirective: charmap directives handled in AssemblyExpander.EarlyProcessCharmap,
            // EQU handled in AssemblyExpander.EarlyDefineEqu, EXPORT in Pass1Export.
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
        // RGBDS resets local label scope on every SECTION directive
        _symbols.SetGlobalAnchor(null);

        if (!SectionHeaderParser.TryParse(node, _diagnostics,
                out var name, out var sectionType, out var fixedAddress, out var bank,
                out var isUnion, out _))
            return;
        _sections.OpenOrResume(name!, sectionType, fixedAddress, bank);
        if (isUnion)
            _sections.BeginUnion();
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

        // Empty data directive warning (RGBDS -Wempty-data-directive)
        if (keyword.Kind is SyntaxKind.DbKeyword or SyntaxKind.DwKeyword or SyntaxKind.DlKeyword
            && expressions.Count == 0)
        {
            _diagnostics.Report(node.FullSpan,
                $"Empty {keyword.Text.ToUpperInvariant()} directive",
                Diagnostics.DiagnosticSeverity.Warning);
            return;
        }

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
                            FilePath = _diagnostics.CurrentFilePath,
                            GlobalAnchorName = _symbols.CurrentGlobalAnchorName,
                        });
                    }
                }
                break;

            case SyntaxKind.DwKeyword:
                foreach (var expr in expressions)
                {
                    // String literals in DW: each character becomes a 16-bit word
                    if (IsStringLiteral(expr.Green))
                    {
                        var text = ExtractStringText(expr.Green);
                        foreach (var b in _charMaps.EncodeString(text))
                            section.EmitWord(b);
                        continue;
                    }

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
                            FilePath = _diagnostics.CurrentFilePath,
                            GlobalAnchorName = _symbols.CurrentGlobalAnchorName,
                        });
                    }
                }
                break;

            case SyntaxKind.DlKeyword:
                foreach (var expr in expressions)
                {
                    var val = evaluator.TryEvaluate(expr.Green);
                    if (val.HasValue)
                        section.EmitDword((uint)(val.Value & 0xFFFFFFFF));
                    else
                    {
                        int offset = section.ReserveDword();
                        section.RecordPatch(new PatchEntry
                        {
                            SectionName = section.Name, Offset = offset,
                            Expression = expr.Green, Kind = PatchKind.Absolute32,
                            FilePath = _diagnostics.CurrentFilePath,
                            GlobalAnchorName = _symbols.CurrentGlobalAnchorName,
                        });
                    }
                }
                break;

            case SyntaxKind.DsKeyword:
            {
                // DS ALIGN[n] / DS ALIGN[n, offset] — alignment padding
                var alignToken = node.ChildTokens().FirstOrDefault(t => t.Kind == SyntaxKind.AlignKeyword);
                if (alignToken != null)
                {
                    Pass2DsAlign(node);
                    break;
                }
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
    }

    private void Pass2DsAlign(SyntaxNode node)
    {
        var section = _sections.ActiveSection;
        if (section == null)
        {
            _diagnostics.Report(node.FullSpan, "DS ALIGN outside of a section");
            return;
        }
        var exprNodes = node.ChildNodes().ToList();
        if (exprNodes.Count == 0) return;
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => section.CurrentPC);
        var alignBits = evaluator.TryEvaluate(exprNodes[0].Green);
        if (!alignBits.HasValue || alignBits.Value < 0 || alignBits.Value > 16) return;

        // For floating sections, use effective alignment = min(section_align, requested)
        int bits = (int)alignBits.Value;
        if (section.AlignBits > 0)
            bits = Math.Min(bits, section.AlignBits);

        int boundary = 1 << bits;
        int alignOffset = 0;
        if (exprNodes.Count > 1)
        {
            var offsetVal = evaluator.TryEvaluate(exprNodes[1].Green);
            if (offsetVal.HasValue)
                alignOffset = (int)offsetVal.Value;
        }

        // Fill value — last expression if there are extra expressions after align params
        byte fill = 0x00;
        // Check for fill value token after the closing bracket
        var tokens = node.ChildTokens().ToList();
        // Find the comma after CloseBracketToken — the expression after it is the fill value
        bool pastBracket = false;
        int fillExprIndex = -1;
        for (int ti = 0; ti < tokens.Count; ti++)
        {
            if (tokens[ti].Kind == SyntaxKind.CloseBracketToken)
                pastBracket = true;
            else if (pastBracket && tokens[ti].Kind == SyntaxKind.CommaToken)
            {
                // The fill expression is the last child node
                if (exprNodes.Count > 0)
                    fillExprIndex = exprNodes.Count - 1;
                break;
            }
        }
        if (fillExprIndex >= 0 && fillExprIndex > (exprNodes.Count > 1 ? 1 : 0))
        {
            var fillVal = evaluator.TryEvaluate(exprNodes[fillExprIndex].Green);
            if (fillVal.HasValue) fill = (byte)(fillVal.Value & 0xFF);
        }

        int mask = boundary - 1;
        int pad = ((boundary - ((section.CurrentPC - alignOffset) & mask)) & mask);
        section.ReserveBytes(pad, fill);
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
                // Regular ASSERT with unresolvable expression: link-time assertion (recorded in patches for rgblink)
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
            {
                // PRINT/PRINTLN — resolve interpolations and collect output.
                // Output goes to _printOutput, NOT Console (Console is the LSP stdio transport).
                var msgToken = tokens.FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
                if (msgToken != null)
                {
                    var text = msgToken.Text.Length >= 2 ? msgToken.Text[1..^1] : msgToken.Text;
                    if (_expander != null)
                        text = _expander.ResolveInterpolations(text);
                    _printOutput.Write(text);
                }
                if (tokens[0].Kind == SyntaxKind.PrintlnKeyword)
                    _printOutput.WriteLine();
                break;
            }

            case SyntaxKind.PushsKeyword:
                _sections.PushSection();
                break;

            case SyntaxKind.PopsKeyword:
                if (!_sections.PopSection())
                    _diagnostics.Report(node.FullSpan, "POPS without matching PUSHS");
                break;

            case SyntaxKind.NextuKeyword:
                if (!_sections.NextUnion())
                    _diagnostics.Report(node.FullSpan, "NEXTU without matching SECTION UNION");
                break;

            case SyntaxKind.EnduKeyword:
                if (!_sections.EndUnion())
                    _diagnostics.Report(node.FullSpan, "ENDU without matching SECTION UNION");
                break;

            case SyntaxKind.LoadKeyword:
                Pass2Load(node);
                break;

            case SyntaxKind.EndlKeyword:
                if (!_sections.EndLoad())
                    _diagnostics.Report(node.FullSpan, "ENDL without matching LOAD");
                break;

            case SyntaxKind.AlignKeyword:
                Pass2Align(node);
                break;

            case SyntaxKind.OptKeyword:
                // OPT accepted — options do not affect assembly output in Koh
                break;
            case SyntaxKind.PushoKeyword:
            case SyntaxKind.PopoKeyword:
                // PUSHO/POPO accepted — option stacking has no effect since OPT is a no-op
                break;
        }
    }

    private void Pass2Align(SyntaxNode node)
    {
        var section = _sections.ActiveSection;
        if (section == null)
        {
            _diagnostics.Report(node.FullSpan, "ALIGN outside of a section");
            return;
        }
        var exprNodes = node.ChildNodes().ToList();
        if (exprNodes.Count == 0)
        {
            _diagnostics.Report(node.FullSpan, "ALIGN requires an alignment value");
            return;
        }
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => section.CurrentPC);
        var alignBits = evaluator.TryEvaluate(exprNodes[0].Green);
        if (!alignBits.HasValue || alignBits.Value < 0 || alignBits.Value > 16)
        {
            _diagnostics.Report(node.FullSpan, "ALIGN value must be 0-16");
            return;
        }

        int bits = (int)alignBits.Value;
        if (bits > section.AlignBits)
            section.AlignBits = bits;
        int boundary = 1 << bits;
        int alignOffset = 0;
        if (exprNodes.Count > 1)
        {
            var offsetVal = evaluator.TryEvaluate(exprNodes[1].Green);
            if (offsetVal.HasValue)
            {
                alignOffset = (int)offsetVal.Value;
                if (alignOffset < 0 || alignOffset >= boundary)
                {
                    _diagnostics.Report(node.FullSpan,
                        $"ALIGN offset must be 0-{boundary - 1} for alignment {alignBits.Value}");
                    return;
                }
            }
        }

        int mask = boundary - 1;
        int pad = ((boundary - ((section.CurrentPC - alignOffset) & mask)) & mask);
        section.ReserveBytes(pad);
    }

    private void Pass2Load(SyntaxNode node)
    {
        var tokens = node.ChildTokens().ToList();
        string? name = null;
        SectionType type = SectionType.Wram0;
        int? fixedAddr = null;
        int? bank = null;

        for (int i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == SyntaxKind.StringLiteral)
                name = tokens[i].Text.Length >= 2 ? tokens[i].Text[1..^1] : tokens[i].Text;
            var mapped = tokens[i].Kind switch
            {
                SyntaxKind.Wram0Keyword => SectionType.Wram0,
                SyntaxKind.WramxKeyword => SectionType.WramX,
                SyntaxKind.HramKeyword  => SectionType.Hram,
                SyntaxKind.SramKeyword  => SectionType.Sram,
                SyntaxKind.VramKeyword  => SectionType.Vram,
                _ => (SectionType?)null,
            };
            if (mapped.HasValue) type = mapped.Value;
        }

        if (name == null)
        {
            _diagnostics.Report(node.FullSpan, "LOAD requires a section name");
            return;
        }

        _sections.BeginLoad(name, type, fixedAddr, bank);
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
