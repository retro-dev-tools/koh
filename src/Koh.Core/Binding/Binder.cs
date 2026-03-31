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
    private int _fixedPointFracBits;
    private Symbol? _savedGlobalAnchorBeforeLoad; // saved global anchor for ENDL scope restoration

    private Func<string, string>? ExpanderResolve =>
        _expander != null ? _expander.ResolveInterpolations : null;

    /// <summary>Fixed-point fractional bits (Q.N). Default is 16.</summary>
    private int _fracBits = 16;
    private readonly Stack<int> _optStack = new();
    private readonly HashSet<string> _declaredSections = new(StringComparer.OrdinalIgnoreCase);

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

        // UTC date/time constants
        var now = DateTime.UtcNow;
        _symbols.DefineConstant("__UTC_YEAR__", now.Year, null);
        _symbols.DefineConstant("__UTC_MONTH__", now.Month, null);
        _symbols.DefineConstant("__UTC_DAY__", now.Day, null);
        _symbols.DefineConstant("__UTC_HOUR__", now.Hour, null);
        _symbols.DefineConstant("__UTC_MINUTE__", now.Minute, null);
        _symbols.DefineConstant("__UTC_SECOND__", now.Second, null);
    }

    public BindingResult Bind(SyntaxTree tree)
    {
        // Pre-define RGBDS built-in constants for conditional assembly compatibility
        DefineRgbdsBuiltins();

        foreach (var d in tree.Diagnostics)
            _diagnostics.Report(d.Span, d.Message, d.Severity);

        // Pre-pass: expand macros, REPT/FOR, IF/ELIF/ELSE/ENDC, INCLUDE into flat list
        _expander = new AssemblyExpander(_diagnostics, _symbols, _fileResolver, _charMaps, _printOutput);
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
        _expander.GetCurrentPC = () => _sections.ActiveSection?.CurrentPC ?? 0;
        _symbols.ResetAnonymousIndex();
        // Wire up section name resolver for {SECTION(@)} in PRINTLN interpolation
        _expander.SectionNameResolver = arg =>
        {
            if (arg == "@") return _sections.ActiveSection?.Name;
            var sym = _symbols.Lookup(arg);
            return sym?.Section;
        };
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

    /// <summary>
    /// Create an ExpressionEvaluator with all resolvers wired up.
    /// </summary>
    private ExpressionEvaluator CreateEvaluator(Func<int> getCurrentPC)
    {
        var eval = new ExpressionEvaluator(_symbols, _diagnostics, getCurrentPC, _fracBits,
            _charMaps, _expander != null ? _expander.ResolveInterpolations : null)
        {
            FracBits = _fracBits,
            EqusResolver = name => _expander?.LookupEqus(name),
            CharlenResolver = s => _charMaps.CharLen(s),
            IncharmapResolver = s => _charMaps.InCharMap(s),
            ReadfileResolver = _expander != null ? _expander.ResolveReadfile : null,
        };
        return eval;
    }

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

    private Symbol? _savedGlobalAnchorBeforeLoadPass1;

    private void Pass1Directive(SyntaxNode node, SectionPCTracker pc)
    {
        var kw = node.ChildTokens().FirstOrDefault();
        if (kw == null) return;

        switch (kw.Kind)
        {
            case SyntaxKind.PushsKeyword:
                pc.PushSection();
                // PUSHS with inline section: PUSHS "name", TYPE
                Pass1PushsSection(node, pc);
                break;
            case SyntaxKind.PopsKeyword:
                pc.PopSection();
                break;
            case SyntaxKind.UnionKeyword:
                pc.BeginUnion();
                break;
            case SyntaxKind.NextuKeyword:
                pc.NextUnion();
                break;
            case SyntaxKind.EnduKeyword:
                pc.EndUnion();
                break;
            case SyntaxKind.LoadKeyword:
                _savedGlobalAnchorBeforeLoadPass1 = _symbols.Lookup(_symbols.CurrentGlobalAnchorName ?? "");
                Pass1Load(node, pc);
                break;
            case SyntaxKind.EndlKeyword:
                pc.EndLoad();
                if (_savedGlobalAnchorBeforeLoadPass1 != null)
                    _symbols.SetGlobalAnchor(_savedGlobalAnchorBeforeLoadPass1);
                break;
            case SyntaxKind.AlignKeyword:
                Pass1Align(node, pc);
                break;
            case SyntaxKind.OptKeyword:
                ParseOptDirective(node);
                break;
            // PUSHO/POPO: no PC impact in Pass 1
        }
    }

    private void Pass1Align(SyntaxNode node, SectionPCTracker pc)
    {
        var exprNodes = node.ChildNodes().ToList();
        if (exprNodes.Count == 0) return;
        var evaluator = CreateEvaluator(() => pc.CurrentPC);
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

    /// <summary>
    /// Extract section-like properties (name, type, fixed address) from a directive node's tokens.
    /// Used by LOAD, PUSHS-with-inline-section, and similar directives.
    /// </summary>
    private static (string? Name, SectionType Type, int? FixedAddr) ParseSectionTokens(SyntaxNode node)
    {
        var tokens = node.ChildTokens().ToList();
        string? name = null;
        SectionType type = SectionType.Wram0;
        int? fixedAddr = null;

        for (int i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == SyntaxKind.StringLiteral)
                name = StripQuotes(tokens[i].Text);
            var mapped = tokens[i].Kind switch
            {
                SyntaxKind.Rom0Keyword  => SectionType.Rom0,
                SyntaxKind.RomxKeyword  => SectionType.RomX,
                SyntaxKind.Wram0Keyword => SectionType.Wram0,
                SyntaxKind.WramxKeyword => SectionType.WramX,
                SyntaxKind.HramKeyword  => SectionType.Hram,
                SyntaxKind.SramKeyword  => SectionType.Sram,
                SyntaxKind.VramKeyword  => SectionType.Vram,
                SyntaxKind.OamKeyword   => SectionType.Oam,
                _ => (SectionType?)null,
            };
            if (mapped.HasValue) type = mapped.Value;
            if (tokens[i].Kind == SyntaxKind.OpenBracketToken && i + 1 < tokens.Count)
            {
                var addrVal = ExpressionEvaluator.ParseNumber(tokens[i + 1].Text);
                if (addrVal.HasValue) fixedAddr = (int)addrVal.Value;
            }
        }

        return (name, type, fixedAddr);
    }

    private void Pass1Load(SyntaxNode node, SectionPCTracker pc)
    {
        var (name, _, fixedAddr) = ParseSectionTokens(node);
        if (name != null)
            pc.BeginLoad(name, fixedAddr ?? 0);
    }

    private void Pass1PushsSection(SyntaxNode node, SectionPCTracker pc)
    {
        var (name, _, fixedAddr) = ParseSectionTokens(node);
        if (name != null)
            pc.SetActive(name, fixedAddr ?? 0);
    }

    /// <summary>
    /// Pass 2 label tracking: advance the global anchor so local label references
    /// in expressions resolve against the correct scope.
    /// </summary>
    private void Pass2Label(SyntaxNode node)
    {
        var first = node.ChildTokens().FirstOrDefault();
        if (first == null) return;

        // Anonymous label: advance the anonymous label index
        if (first.Kind == SyntaxKind.ColonToken)
        {
            _symbols.AdvanceAnonymousIndex();
            return;
        }

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

        // Anonymous label: bare colon (the parser produces a LabelDeclaration with just ColonToken)
        if (tokens[0].Kind == SyntaxKind.ColonToken && tokens.Count == 1)
        {
            _symbols.DefineAnonymousLabel(pc.LabelPC, pc.LabelSectionName, node);
            return;
        }

        var name = tokens[0].Text;

        // Label outside section check
        if (!pc.HasActiveSection)
        {
            _diagnostics.Report(node.FullSpan, $"Label '{name}' declared outside of a section");
            return;
        }

        // Local label without global parent
        if (name.StartsWith('.') && !_symbols.HasGlobalAnchor)
        {
            _diagnostics.Report(node.FullSpan, $"Local label '{name}' declared without a preceding global label");
            return;
        }

        // In LOAD blocks, labels get addresses from the load section
        var sym = _symbols.DefineLabel(name, pc.LabelPC, pc.LabelSectionName, node);
        if (tokens.Count >= 2 && tokens[1].Kind == SyntaxKind.DoubleColonToken)
            sym.Visibility = SymbolVisibility.Exported;
    }

    private void Pass1Section(SyntaxNode node, SectionPCTracker pc)
    {
        if (!SectionHeaderParser.TryParse(node, _diagnostics,
                out var name, out var type, out var fixedAddress, out _,
                out var isUnion, out _, out _, out _))
            return;
        pc.SetActive(name!, fixedAddress ?? 0, type);
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
                int byteCount = expressions.Count == 0 ? 1 : 0; // empty db = 1 byte
                foreach (var expr in expressions)
                {
                    if (IsStringLiteral(expr.Green))
                        byteCount += _charMaps.EncodeString(ExtractStringText(expr.Green)).Length;
                    else
                        byteCount++;
                }
                pc.Advance(byteCount);
                pc.AdvanceLoad(byteCount);
                CheckSectionOverflow(node, pc);
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
                if (byteCount == 0) byteCount = 2; // empty DW still reserves 2 bytes
                pc.Advance(byteCount);
                pc.AdvanceLoad(byteCount);
                CheckSectionOverflow(node, pc);
                break;
            }
            case SyntaxKind.DlKeyword:
            {
                int size = expressions.Count == 0 ? 4 : expressions.Count * 4;
                pc.Advance(size);
                pc.AdvanceLoad(size);
                CheckSectionOverflow(node, pc);
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
                    var evaluator = CreateEvaluator(() => pc.CurrentPC);
                    var sizeVal = evaluator.TryEvaluate(expressions[0].Green);
                    int dsSize = sizeVal.HasValue ? (int)sizeVal.Value : 0;
                    pc.Advance(dsSize);
                    pc.AdvanceLoad(dsSize);
                    CheckSectionOverflow(node, pc);
                }
                break;
            }
        }
    }

    /// <summary>
    /// After advancing the PC in Pass 1, check that the section offset has not exceeded
    /// the maximum byte capacity for the section type.
    /// </summary>
    private void CheckSectionOverflow(SyntaxNode node, SectionPCTracker pc)
    {
        var type = pc.ActiveSectionType;
        if (type == null) return;
        int maxSize = SectionPCTracker.MaxSizeForType(type.Value);
        int offset = pc.ActiveSectionOffset;
        if (offset > maxSize)
            _diagnostics.Report(node.FullSpan,
                $"Section '{pc.ActiveSectionName}' overflows: ${offset:X4} bytes exceeds {type.Value} maximum of ${maxSize:X4} bytes");
    }

    private void Pass1DsAlign(SyntaxNode node, SectionPCTracker pc)
    {
        var exprNodes = node.ChildNodes().ToList();
        if (exprNodes.Count == 0) return;
        var evaluator = CreateEvaluator(() => pc.CurrentPC);
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
                Pass2Instruction(node, en.WasInConditional);
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

            // Parse optional start offset and length from token stream
            // INCBIN "file"[, start[, length]]
            var allTokens = node.ChildTokens().ToList();
            var evaluator = CreateEvaluator(() => section.CurrentPC);
            int startOffset = 0;
            int? length = null;
            // Find number tokens after the string literal, separated by commas
            var numTokens = new List<long>();
            bool pastString = false;
            for (int ti = 0; ti < allTokens.Count; ti++)
            {
                if (allTokens[ti].Kind == SyntaxKind.StringLiteral) { pastString = true; continue; }
                if (!pastString) continue;
                if (allTokens[ti].Kind == SyntaxKind.CommaToken) continue;
                if (allTokens[ti].Kind == SyntaxKind.NumberLiteral)
                {
                    var v = ExpressionEvaluator.ParseNumber(allTokens[ti].Text);
                    if (v.HasValue) numTokens.Add(v.Value);
                }
                else if (allTokens[ti].Kind == SyntaxKind.MinusToken && ti + 1 < allTokens.Count
                    && allTokens[ti + 1].Kind == SyntaxKind.NumberLiteral)
                {
                    var v = ExpressionEvaluator.ParseNumber(allTokens[ti + 1].Text);
                    if (v.HasValue) numTokens.Add(-v.Value);
                    ti++; // skip number
                }
            }
            // Also try child expression nodes
            var exprNodes = node.ChildNodes().ToList();
            if (numTokens.Count == 0 && exprNodes.Count > 0)
            {
                foreach (var en in exprNodes)
                {
                    var val = evaluator.TryEvaluate(en.Green);
                    if (val.HasValue) numTokens.Add(val.Value);
                }
            }
            if (numTokens.Count >= 1) startOffset = (int)numTokens[0];
            if (numTokens.Count >= 2) length = (int)numTokens[1];

            // Validate INCBIN ranges
            if (startOffset < 0)
            {
                _diagnostics.Report(node.FullSpan, $"INCBIN start offset {startOffset} is negative");
                return;
            }
            if (startOffset > bytes.Length)
            {
                _diagnostics.Report(node.FullSpan, $"INCBIN start offset {startOffset} exceeds file size {bytes.Length}");
                return;
            }
            int actualLength = length ?? (bytes.Length - startOffset);
            if (startOffset + actualLength > bytes.Length)
            {
                _diagnostics.Report(node.FullSpan, $"INCBIN range ({startOffset}+{actualLength}) exceeds file size {bytes.Length}");
                return;
            }

            for (int bi = startOffset; bi < startOffset + actualLength; bi++)
                section.EmitByte(bytes[bi]);
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

        // If we're in a LOAD block, a new SECTION implicitly terminates it
        if (_sections.IsInLoad)
        {
            _diagnostics.Report(node.FullSpan,
                "Unterminated LOAD block: SECTION implicitly ends LOAD",
                Diagnostics.DiagnosticSeverity.Warning);
            _sections.EndLoad();
        }

        if (!SectionHeaderParser.TryParse(node, _diagnostics,
                out var name, out var sectionType, out var fixedAddress, out var bank,
                out var isUnion, out var isFragment,
                out var sectionAlignBits, out var sectionAlignOffset))
            return;

        // Validate alignment parameters from the SECTION header
        ValidateSectionConstraints(node, name!, sectionType, fixedAddress, bank, isUnion, isFragment, sectionAlignBits, sectionAlignOffset);

        // Cannot reopen a section that is currently suspended on the PUSHS stack
        if (_sections.IsSectionOnStack(name!))
        {
            _diagnostics.Report(node.FullSpan, $"Section '{name}' is currently on the PUSHS stack and cannot be reopened");
            return;
        }

        _sections.OpenOrResume(name!, sectionType, fixedAddress, bank);
        if (_expander != null)
            _expander.CurrentSectionName = name;

        // Apply ALIGN[N[, offset]] from section header to the section buffer
        if (sectionAlignBits.HasValue)
        {
            var section = _sections.ActiveSection;
            if (section != null && sectionAlignBits.Value > section.AlignBits)
            {
                section.AlignBits = sectionAlignBits.Value;
                section.AlignOffset = sectionAlignOffset ?? 0;
            }
        }

        if (isUnion)
            _sections.BeginUnion();
    }

    private void ValidateSectionConstraints(SyntaxNode node, string name, SectionType sectionType,
        int? fixedAddress, int? bank, bool isUnion, bool isFragment,
        int? sectionAlignBits = null, int? sectionAlignOffset = null)
    {
        // Validate fixed address range
        if (fixedAddress.HasValue)
        {
            var (lo, hi) = GetSectionAddressRange(sectionType);
            if (fixedAddress.Value < lo || fixedAddress.Value > hi)
            {
                _diagnostics.Report(node.FullSpan,
                    $"Fixed address ${fixedAddress.Value:X4} is outside the range for {sectionType} (${lo:X4}-${hi:X4})");
            }
        }

        // Validate bank
        if (bank.HasValue)
        {
            switch (sectionType)
            {
                case SectionType.Rom0:
                case SectionType.Wram0:
                case SectionType.Hram:
                case SectionType.Oam:
                    _diagnostics.Report(node.FullSpan,
                        $"{sectionType} does not support BANK clause");
                    break;
                case SectionType.Vram:
                    if (bank.Value < 0 || bank.Value > 1)
                        _diagnostics.Report(node.FullSpan,
                            $"VRAM bank must be 0 or 1, got {bank.Value}");
                    break;
                case SectionType.WramX:
                    if (bank.Value < 1 || bank.Value > 7)
                        _diagnostics.Report(node.FullSpan,
                            $"WRAMX bank must be 1-7, got {bank.Value}");
                    break;
                case SectionType.Sram:
                    if (bank.Value < 0 || bank.Value > 15)
                        _diagnostics.Report(node.FullSpan,
                            $"SRAM bank must be 0-15, got {bank.Value}");
                    break;
                case SectionType.RomX:
                    if (bank.Value < 1 || bank.Value > 511)
                        _diagnostics.Report(node.FullSpan,
                            $"ROMX bank must be 1-511, got {bank.Value}");
                    break;
            }
        }

        // Validate alignment from the section header (parsed by SectionHeaderParser.TryParse)
        if (sectionAlignBits.HasValue)
        {
            if (sectionAlignBits.Value < 0 || sectionAlignBits.Value > 16)
            {
                _diagnostics.Report(node.FullSpan,
                    $"ALIGN value must be 0-16, got {sectionAlignBits.Value}");
            }
            else
            {
                int boundary = 1 << sectionAlignBits.Value;
                int alignOffset = sectionAlignOffset ?? 0;
                if (alignOffset < 0 || alignOffset >= boundary)
                {
                    _diagnostics.Report(node.FullSpan,
                        $"ALIGN offset must be 0-{boundary - 1} for alignment {sectionAlignBits.Value}, got {alignOffset}");
                }
                else if (fixedAddress.HasValue)
                {
                    // Check fixed address is compatible with requested alignment
                    if ((fixedAddress.Value % boundary) != alignOffset)
                    {
                        _diagnostics.Report(node.FullSpan,
                            $"Fixed address ${fixedAddress.Value:X4} is incompatible with ALIGN[{sectionAlignBits.Value}, {alignOffset}] (${fixedAddress.Value:X4} % {boundary} = {fixedAddress.Value % boundary}, expected {alignOffset})");
                    }
                }
            }
        }

        // Check for duplicate non-fragment, non-union section names
        // A second SECTION directive with the same name (non-fragment, non-union) is an error
        // unless it matches an existing section and serves as section resuming via PUSHS/POPS.
        if (!isFragment && !isUnion)
        {
            if (_declaredSections.Contains(name) && _sections.AllSections.ContainsKey(name))
            {
                // Check if we're in the same contiguous section — that's the duplicate case.
                // If we used PUSHS/POPS to get here, it's section resuming (allowed).
                var existing = _sections.AllSections[name];
                if (_sections.ActiveSection == existing)
                {
                    _diagnostics.Report(node.FullSpan,
                        $"Section '{name}' already defined");
                }
            }
            _declaredSections.Add(name);
        }

        // Check for UNION in ROM (not allowed — UNION is only for RAM types)
        if (isUnion)
        {
            if (sectionType is SectionType.Rom0 or SectionType.RomX)
            {
                _diagnostics.Report(node.FullSpan,
                    "UNION sections cannot be ROM type — use WRAM0, WRAMX, HRAM, SRAM, or VRAM");
            }
        }

        // Validate fragment constraints are consistent
        if (isFragment && _sections.AllSections.TryGetValue(name, out var existingFrag))
        {
            if (fixedAddress.HasValue && existingFrag.FixedAddress.HasValue &&
                fixedAddress.Value != existingFrag.FixedAddress.Value)
            {
                _diagnostics.Report(node.FullSpan,
                    $"FRAGMENT '{name}' has conflicting fixed addresses: ${existingFrag.FixedAddress.Value:X4} vs ${fixedAddress.Value:X4}");
            }
            // Check new alignment requirement against existing section's fixed address
            if (sectionAlignBits.HasValue && existingFrag.FixedAddress.HasValue)
            {
                int boundary = 1 << sectionAlignBits.Value;
                int expectedOffset = sectionAlignOffset ?? 0;
                if ((existingFrag.FixedAddress.Value % boundary) != expectedOffset)
                {
                    _diagnostics.Report(node.FullSpan,
                        $"FRAGMENT '{name}' alignment ALIGN[{sectionAlignBits.Value}] is incompatible with fixed address ${existingFrag.FixedAddress.Value:X4}");
                }
            }
            // Check fragment alignment compatibility: if both the existing fragment and the
            // new fragment have alignment requirements, verify the current offset is compatible.
            // Two ALIGN[8] fragments with a non-256-byte-aligned offset between them are incompatible.
            if (sectionAlignBits.HasValue && existingFrag.AlignBits > 0 && existingFrag.CurrentOffset > 0)
            {
                int newBoundary = 1 << sectionAlignBits.Value;
                if (existingFrag.CurrentOffset % newBoundary != (sectionAlignOffset ?? 0))
                {
                    _diagnostics.Report(node.FullSpan,
                        $"FRAGMENT '{name}' alignment ALIGN[{sectionAlignBits.Value}] is incompatible with current fragment offset {existingFrag.CurrentOffset}");
                }
            }
            // Check new fixed address against existing section's alignment requirement
            if (fixedAddress.HasValue && existingFrag.AlignBits > 0)
            {
                int boundary = 1 << existingFrag.AlignBits;
                int expectedOffset = existingFrag.AlignOffset;
                if ((fixedAddress.Value % boundary) != expectedOffset)
                {
                    _diagnostics.Report(node.FullSpan,
                        $"FRAGMENT '{name}' fixed address ${fixedAddress.Value:X4} is incompatible with existing alignment ALIGN[{existingFrag.AlignBits}]");
                }
            }
        }

        // Validate UNION constraints are consistent
        if (isUnion && _sections.AllSections.TryGetValue(name, out var existingUnion))
        {
            if (fixedAddress.HasValue && existingUnion.FixedAddress.HasValue &&
                fixedAddress.Value != existingUnion.FixedAddress.Value)
            {
                _diagnostics.Report(node.FullSpan,
                    $"UNION '{name}' has conflicting fixed addresses: ${existingUnion.FixedAddress.Value:X4} vs ${fixedAddress.Value:X4}");
            }
            // Check new alignment requirement against existing UNION's fixed address
            if (sectionAlignBits.HasValue && existingUnion.FixedAddress.HasValue)
            {
                int boundary = 1 << sectionAlignBits.Value;
                int expectedOffset = sectionAlignOffset ?? 0;
                if ((existingUnion.FixedAddress.Value % boundary) != expectedOffset)
                {
                    _diagnostics.Report(node.FullSpan,
                        $"UNION '{name}' alignment ALIGN[{sectionAlignBits.Value}] is incompatible with fixed address ${existingUnion.FixedAddress.Value:X4}");
                }
            }
            if (sectionAlignBits.HasValue && sectionAlignBits.Value > 15)
            {
                _diagnostics.Report(node.FullSpan,
                    $"UNION '{name}' alignment ALIGN[{sectionAlignBits.Value}] is unattainable");
            }
        }
    }

    private static (int lo, int hi) GetSectionAddressRange(SectionType type) => type switch
    {
        SectionType.Rom0 => (0x0000, 0x3FFF),
        SectionType.RomX => (0x4000, 0x7FFF),
        SectionType.Wram0 => (0xC000, 0xCFFF),
        SectionType.WramX => (0xD000, 0xDFFF),
        SectionType.Vram => (0x8000, 0x9FFF),
        SectionType.Hram => (0xFF80, 0xFFFE),
        SectionType.Sram => (0xA000, 0xBFFF),
        SectionType.Oam => (0xFE00, 0xFE9F),
        _ => (0, 0xFFFF),
    };

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
        var evaluator = CreateEvaluator(() => section.CurrentPC);

        // Check: data (db/dw/dl) in RAM sections (only ds is allowed)
        if (keyword.Kind is SyntaxKind.DbKeyword or SyntaxKind.DwKeyword or SyntaxKind.DlKeyword)
        {
            if (section.Type is SectionType.Wram0 or SectionType.WramX or SectionType.Hram
                or SectionType.Sram or SectionType.Vram or SectionType.Oam)
            {
                if (expressions.Count > 0)
                {
                    _diagnostics.Report(node.FullSpan,
                        $"Cannot use {keyword.Text.ToUpperInvariant()} with data in {section.Type} section — only DS is allowed in RAM sections");
                    return;
                }
            }
        }

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
                    {
                        // Truncation warning for values that don't fit in 8 bits
                        if (val.Value > 255 || val.Value < -128)
                            _diagnostics.Report(expr.FullSpan,
                                $"Value {val.Value} truncated to 8-bit range",
                                DiagnosticSeverity.Warning);
                        section.EmitByte((byte)(val.Value & 0xFF));
                    }
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
                    // String literals in DW: encode through character map
                    if (IsStringLiteral(expr.Green))
                    {
                        var text = ExtractStringText(expr.Green);
                        var encoded = _charMaps.EncodeString(text);
                        // Emit charmap bytes as little-endian words (pad odd byte with 0)
                        for (int bi = 0; bi < encoded.Length; bi += 2)
                        {
                            byte lo = encoded[bi];
                            byte hi = bi + 1 < encoded.Length ? encoded[bi + 1] : (byte)0;
                            section.EmitWord((ushort)(lo | (hi << 8)));
                        }
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
                        section.EmitLong((uint)(val.Value & 0xFFFFFFFF));
                    else
                    {
                        int offset = section.ReserveLong();
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
                    // Check for DS ALIGN[n] syntax
                    if (IsDsAlign(expressions[0]))
                    {
                        EmitDsAlign(section, evaluator, expressions);
                        break;
                    }

                    var countVal = evaluator.TryEvaluate(expressions[0].Green);
                    if (countVal.HasValue)
                    {
                        int dsCount = (int)countVal.Value;
                        if (expressions.Count > 1)
                        {
                            // Re-evaluate fill per byte so @ (PC) advances correctly
                            var fillExpr = expressions[1].Green;
                            for (int i = 0; i < dsCount; i++)
                            {
                                var fillVal = evaluator.TryEvaluate(fillExpr);
                                section.EmitByte(fillVal.HasValue ? (byte)(fillVal.Value & 0xFF) : (byte)0);
                            }
                        }
                        else
                        {
                            section.ReserveBytes(dsCount);
                        }
                    }
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
        var evaluator = CreateEvaluator(() => section.CurrentPC);
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
        // For floating sections, the ALIGN directive records the section's alignment offset
        // (i.e., the base address the linker will use). DS ALIGN must account for this so that
        // the padding targets an address where (sectionBase + pc + pad) % boundary == alignOffset.
        // sectionBase % sectionAlignBoundary == section.AlignOffset, so we subtract it from the target.
        int sectionAlignBase = section.FixedAddress ?? 0;
        int pad;
        if (section.FixedAddress.HasValue)
        {
            // Fixed-address section: use absolute address directly
            pad = ((boundary - ((section.CurrentPC - alignOffset) & mask)) & mask);
        }
        else if (section.AlignBits > 0)
        {
            // Floating section with known alignment: account for the section's base offset
            pad = ((boundary - ((section.CurrentPC + section.AlignOffset - alignOffset) & mask)) & mask);
        }
        else
        {
            pad = ((boundary - ((section.CurrentPC - alignOffset) & mask)) & mask);
        }
        section.ReserveBytes(pad, fill);
    }

    /// <summary>
    /// Check if a DS expression is DS ALIGN[n, offset] syntax.
    /// The parser produces a FunctionCallExpression with AlignKeyword for "align[n]".
    /// </summary>
    private static bool IsDsAlign(SyntaxNode expr)
    {
        var green = expr.Green;
        // DS ALIGN[...] is parsed as a function call with AlignKeyword
        if (green.Kind == SyntaxKind.FunctionCallExpression)
        {
            var kw = ((Syntax.InternalSyntax.GreenNode)green).GetChild(0);
            if (kw is Syntax.InternalSyntax.GreenToken t && t.Kind == SyntaxKind.AlignKeyword)
                return true;
        }
        return false;
    }

    private void EmitDsAlign(SectionBuffer section, ExpressionEvaluator evaluator,
        List<SyntaxNode> expressions)
    {
        // DS ALIGN[bits, offset], fill
        var alignNode = (Syntax.InternalSyntax.GreenNode)expressions[0].Green;
        // Arguments are at indices 2, 4, ...
        var bitsArg = alignNode.ChildCount > 2 ? alignNode.GetChild(2) : null;
        var offsetArg = alignNode.ChildCount > 4 ? alignNode.GetChild(4) : null;

        int bits = 0;
        int alignOfs = 0;
        if (bitsArg != null)
        {
            var v = evaluator.TryEvaluate(bitsArg);
            if (v.HasValue) bits = (int)v.Value;
        }
        if (offsetArg != null)
        {
            var v = evaluator.TryEvaluate(offsetArg);
            if (v.HasValue) alignOfs = (int)v.Value;
        }

        byte fill = 0x00;
        if (expressions.Count > 1)
        {
            var fillVal = evaluator.TryEvaluate(expressions[1].Green);
            if (fillVal.HasValue) fill = (byte)(fillVal.Value & 0xFF);
        }

        if (bits <= 0) return;
        int boundary = 1 << bits;
        int bitmask = boundary - 1;
        int alignPad = ((boundary - ((section.CurrentPC - alignOfs) & bitmask)) & bitmask);
        section.ReserveBytes(alignPad, fill);
    }

    private void Pass2Instruction(SyntaxNode node, bool wasInConditional = false)
    {
        var section = _sections.ActiveSection;
        if (section == null)
        {
            // Instructions inside a conditional block that precede the first section declaration
            // are silently tolerated as warnings (RGBDS behaviour for pre-section conditional code).
            // Top-level instructions with no section context remain hard errors.
            var severity = wasInConditional ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error;
            _diagnostics.Report(node.FullSpan, "Instruction outside of a section", severity);
            return;
        }

        // Check: code in RAM sections
        if (section.Type is SectionType.Wram0 or SectionType.WramX or SectionType.Hram
            or SectionType.Sram or SectionType.Vram or SectionType.Oam)
        {
            _diagnostics.Report(node.FullSpan,
                $"Cannot use instructions in {section.Type} section — only DS is allowed in RAM sections");
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

    /// <summary>
    /// Recursively scan a green subtree for CurrentAddressToken (@).
    /// Used to detect invalid use of @ in PRINT/PRINTLN context.
    /// </summary>
    private static bool ContainsCurrentAddressToken(GreenNodeBase green)
    {
        if (green is GreenToken tok)
            return tok.Kind is SyntaxKind.CurrentAddressToken or SyntaxKind.AtToken;
        for (int i = 0; i < green.ChildCount; i++)
        {
            var child = green.GetChild(i);
            if (child != null && ContainsCurrentAddressToken(child))
                return true;
        }
        return false;
    }

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
                var evaluator = CreateEvaluator(() => section?.CurrentPC ?? 0);
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
                var exprNodes = node.ChildNodes().ToList();
                if (exprNodes.Count > 0)
                {
                    var section = _sections.ActiveSection;
                    // @ is not a constant in PRINTLN context (floating sections don't have fixed
                    // addresses at assembly time, and RGBDS rejects @ in PRINTLN arguments).
                    if (ContainsCurrentAddressToken(exprNodes[0].Green))
                    {
                        _diagnostics.Report(node.FullSpan,
                            "The current-address token '@' is not a constant and cannot be used in PRINT/PRINTLN");
                        break;
                    }
                    var evaluator = CreateEvaluator(() => section?.CurrentPC ?? 0);
                    evaluator.RejectCurrentAddress = true;
                    // Try string evaluation first (for expressions like strupr(#s) ++ "!")
                    var strVal = evaluator.TryEvaluateString(exprNodes[0].Green);
                    if (strVal != null)
                    {
                        if (_expander != null)
                            strVal = _expander.ResolveInterpolations(strVal);
                        _printOutput.Write(strVal);
                    }
                    else
                    {
                        // Fall back to looking for string literal token
                        var msgToken = tokens.FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
                        if (msgToken != null)
                        {
                            var text = ExpressionEvaluator.InterpretStringLiteral(msgToken.Text);
                            if (_expander != null)
                                text = _expander.ResolveInterpolations(text);
                            _printOutput.Write(text);
                        }
                    }
                }
                if (tokens[0].Kind == SyntaxKind.PrintlnKeyword)
                    _printOutput.WriteLine();
                break;
            }

            case SyntaxKind.PushsKeyword:
                _sections.PushSection();
                Pass2PushsSection(node);
                break;

            case SyntaxKind.PopsKeyword:
                if (!_sections.PopSection())
                    _diagnostics.Report(node.FullSpan, "POPS without matching PUSHS");
                break;

            case SyntaxKind.UnionKeyword:
                _sections.BeginUnion();
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
                // Save current global anchor before entering LOAD block so ENDL can restore it
                _savedGlobalAnchorBeforeLoad = _symbols.Lookup(_symbols.CurrentGlobalAnchorName ?? "");
                Pass2Load(node);
                break;

            case SyntaxKind.EndlKeyword:
                if (!_sections.EndLoad())
                    _diagnostics.Report(node.FullSpan, "ENDL without matching LOAD");
                // Restore global anchor to ROM-side scope after ENDL
                if (_savedGlobalAnchorBeforeLoad != null)
                    _symbols.SetGlobalAnchor(_savedGlobalAnchorBeforeLoad);
                break;

            case SyntaxKind.AlignKeyword:
                Pass2Align(node);
                break;

            case SyntaxKind.OptKeyword:
                ProcessOpt(tokens);
                break;
            case SyntaxKind.PushoKeyword:
                _optStack.Push(_fracBits);
                break;
            case SyntaxKind.PopoKeyword:
                if (_optStack.Count > 0)
                    _fracBits = _optStack.Pop();
                else
                    _diagnostics.Report(node.FullSpan, "POPO without matching PUSHO");
                break;
        }
    }

    private void ProcessOpt(IReadOnlyList<SyntaxToken> tokens)
    {
        // OPT Q.N — set fixed-point precision
        // Tokens: OptKeyword, IdentifierToken("Q"), DotToken("."), NumberLiteral("N")
        // OPT Wno-div, OPT Wno-unmapped-char, OPT p$XX — accepted silently
        for (int i = 1; i < tokens.Count; i++)
        {
            var text = tokens[i].Text;
            // Check for "Q" followed by "." and a number
            if (text.Equals("Q", StringComparison.OrdinalIgnoreCase)
                && i + 2 < tokens.Count
                && tokens[i + 1].Kind == SyntaxKind.DotToken
                && tokens[i + 2].Kind == SyntaxKind.NumberLiteral)
            {
                if (int.TryParse(tokens[i + 2].Text, out var bits) && bits >= 1 && bits <= 31)
                    _fracBits = bits;
                i += 2; // skip dot and number
            }
            // Also handle single-token form "Q.N" (unlikely with lexer but just in case)
            else if (text.StartsWith("Q.", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
            {
                if (int.TryParse(text.AsSpan(2), out var bits) && bits >= 1 && bits <= 31)
                    _fracBits = bits;
            }
            // Known OPT letters: b (binary digits), g (graphics), p (pad byte), W (warnings)
            else if (tokens[i].Kind == SyntaxKind.IdentifierToken && text.Length >= 1)
            {
                char letter = char.ToLowerInvariant(text[0]);
                if (letter is not ('b' or 'g' or 'p' or 'w' or 'q'))
                {
                    _diagnostics.Report(default, $"Unknown OPT option: '{text}'");
                }
                else if (letter == 'b')
                {
                    // b.XX format: skip dot and value tokens
                    if (i + 1 < tokens.Count && tokens[i + 1].Kind == SyntaxKind.DotToken)
                    {
                        i += 2; // skip dot and value
                    }
                    else if (text.Length > 1 && !text.Contains('.'))
                    {
                        _diagnostics.Report(default, $"Invalid OPT binary digit spec: '{text}'");
                    }
                }
                else if (letter == 'p')
                {
                    // p$XX — pad byte. Skip any following tokens that are part of the value
                    if (i + 1 < tokens.Count && tokens[i + 1].Kind == SyntaxKind.NumberLiteral)
                        i++;
                    else if (i + 1 < tokens.Count && tokens[i + 1].Kind == SyntaxKind.CurrentAddressToken)
                    {
                        // p$XX where $ was lexed as CurrentAddressToken followed by hex digits
                        i++;
                        if (i + 1 < tokens.Count && tokens[i + 1].Kind == SyntaxKind.NumberLiteral)
                            i++;
                    }
                }
                else if (letter == 'w')
                {
                    // W-option: skip all following tokens that are part of the warning flag name
                    // e.g., Wno-unmapped-char → Wno, -, unmapped, -, char
                    while (i + 1 < tokens.Count && tokens[i + 1].Kind is SyntaxKind.MinusToken
                        or SyntaxKind.IdentifierToken)
                    {
                        i++;
                        if (tokens[i].Kind == SyntaxKind.IdentifierToken)
                            continue;
                        // MinusToken — skip and continue looking for next identifier part
                    }
                }
            }
        }
    }

    private void ParseOptDirective(SyntaxNode node)
    {
        // Look for Q.N pattern in OPT children: OptKeyword, IdentifierToken("Q"), DotToken, NumberLiteral("N")
        var children = node.ChildNodes().ToList();
        for (int i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Green is GreenToken { Kind: SyntaxKind.IdentifierToken } ident &&
                ident.Text.Equals("Q", StringComparison.OrdinalIgnoreCase))
            {
                // Look for . N after Q
                if (i + 2 < children.Count &&
                    children[i + 1].Green is GreenToken { Kind: SyntaxKind.DotToken } &&
                    children[i + 2].Green is GreenToken { Kind: SyntaxKind.NumberLiteral } numToken &&
                    int.TryParse(numToken.Text, out var bits))
                {
                    _fixedPointFracBits = bits;
                    return;
                }
            }
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
        var evaluator = CreateEvaluator(() => section.CurrentPC);
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

        // Record this offset as the section's alignment base offset (for DS ALIGN calculations).
        // Only update if this is the tightest alignment seen so far (highest bits).
        if (bits == section.AlignBits)
            section.AlignOffset = alignOffset;

        int mask = boundary - 1;
        int pad = ((boundary - ((section.CurrentPC - alignOffset) & mask)) & mask);
        section.ReserveBytes(pad);
    }

    private void Pass2PushsSection(SyntaxNode node)
    {
        var (name, type, fixedAddr) = ParseSectionTokens(node);
        if (name != null)
            _sections.OpenOrResume(name, type, fixedAddr);
    }

    private void Pass2Load(SyntaxNode node)
    {
        // If we're already in a LOAD block, a new LOAD implicitly terminates it
        if (_sections.IsInLoad)
        {
            _diagnostics.Report(node.FullSpan,
                "Unterminated LOAD block: new LOAD implicitly ends previous LOAD",
                Diagnostics.DiagnosticSeverity.Warning);
        }

        var (name, type, fixedAddr) = ParseSectionTokens(node);

        if (name == null)
        {
            _diagnostics.Report(node.FullSpan, "LOAD requires a section name");
            return;
        }

        // LOAD into ROM is not allowed
        if (type is SectionType.Rom0 or SectionType.RomX)
        {
            _diagnostics.Report(node.FullSpan, "LOAD block cannot target ROM section type");
            return;
        }

        _sections.BeginLoad(name, type, fixedAddr, bank: null);
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
        // Handle raw string delimiters: #"""...""" and #"..."
        if (text.StartsWith("#\"\"\"") && text.EndsWith("\"\"\""))
            return text[4..^3];
        if (text.StartsWith("#\"") && text.EndsWith("\""))
            return text[2..^1];
        var raw = text.Length >= 2 ? text[1..^1] : text; // strip quotes
        return ExpressionEvaluator.UnescapeString(raw);
    }
}
