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

        int boundary = 1 << (int)alignBits.Value;
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

        var name = tokens[0].Text;

        // Check: label outside section — only for global labels that aren't EQU-style
        if (pc.ActiveSectionName == null && !name.StartsWith('.'))
        {
            _diagnostics.Report(node.FullSpan, $"Label '{name}' defined outside of a section");
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
                int size = expressions.Count * 2;
                pc.Advance(size);
                pc.AdvanceLoad(size);
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
                if (expressions.Count > 0)
                {
                    var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => pc.CurrentPC);

                    // Check for DS ALIGN[n] syntax
                    if (IsDsAlign(expressions[0]))
                    {
                        // Parse alignment from the function call node
                        var alignNode = (Syntax.InternalSyntax.GreenNode)expressions[0].Green;
                        var bitsArg = alignNode.ChildCount > 2 ? alignNode.GetChild(2) : null;
                        var offsetArg = alignNode.ChildCount > 4 ? alignNode.GetChild(4) : null;
                        int bits = 0, alignOfs = 0;
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
                        if (bits > 0)
                        {
                            int boundary = 1 << bits;
                            int mask = boundary - 1;
                            int pad = ((boundary - ((pc.CurrentPC - alignOfs) & mask)) & mask);
                            pc.Advance(pad);
                            pc.AdvanceLoad(pad);
                        }
                        break;
                    }

                    var sizeVal = evaluator.TryEvaluate(expressions[0].Green);
                    int dsSize = sizeVal.HasValue ? (int)sizeVal.Value : 0;
                    pc.Advance(dsSize);
                    pc.AdvanceLoad(dsSize);
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
                out var isUnion, out var isFragment))
            return;

        // Validate alignment parameters from the SECTION header
        ValidateSectionConstraints(node, name!, sectionType, fixedAddress, bank, isUnion, isFragment);

        _sections.OpenOrResume(name!, sectionType, fixedAddress, bank);
        if (isUnion)
            _sections.BeginUnion();
    }

    private void ValidateSectionConstraints(SyntaxNode node, string name, SectionType sectionType,
        int? fixedAddress, int? bank, bool isUnion, bool isFragment)
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

        // Parse ALIGN from the section header tokens
        var tokens = node.ChildTokens().ToList();
        int? alignBits = null;
        int? alignOffset = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == SyntaxKind.AlignKeyword)
            {
                // ALIGN[bits[, offset]]
                if (i + 2 < tokens.Count && tokens[i + 1].Kind == SyntaxKind.OpenBracketToken)
                {
                    // Find the number tokens inside brackets
                    int j = i + 2;
                    var nums = new List<int>();
                    while (j < tokens.Count && tokens[j].Kind != SyntaxKind.CloseBracketToken)
                    {
                        if (tokens[j].Kind == SyntaxKind.NumberLiteral &&
                            SectionHeaderParser.TryParseIntegerLiteral(tokens[j].Text, out int val))
                            nums.Add(val);
                        j++;
                    }
                    if (nums.Count > 0) alignBits = nums[0];
                    if (nums.Count > 1) alignOffset = nums[1];
                }
            }
        }

        // Validate alignment
        if (alignBits.HasValue)
        {
            if (alignBits.Value < 0 || alignBits.Value > 16)
            {
                _diagnostics.Report(node.FullSpan,
                    $"ALIGN value must be 0-16, got {alignBits.Value}");
            }
            else if (alignOffset.HasValue)
            {
                int boundary = 1 << alignBits.Value;
                if (alignOffset.Value < 0 || alignOffset.Value >= boundary)
                {
                    _diagnostics.Report(node.FullSpan,
                        $"ALIGN offset must be 0-{boundary - 1} for alignment {alignBits.Value}, got {alignOffset.Value}");
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
            if (alignBits.HasValue && alignBits.Value > 15)
            {
                _diagnostics.Report(node.FullSpan,
                    $"UNION '{name}' alignment ALIGN[{alignBits.Value}] is unattainable");
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
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => section.CurrentPC)
            { CharMaps = _charMaps, FileResolver = _fileResolver };

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

        // Check: instructions in RAM sections
        // (handled in Pass2Instruction)

        // Empty data directive warning (db/dw/dl with no args)
        if (expressions.Count == 0 && keyword.Kind is SyntaxKind.DbKeyword or SyntaxKind.DwKeyword or SyntaxKind.DlKeyword)
        {
            _diagnostics.Report(node.FullSpan,
                $"Empty {keyword.Text.ToUpperInvariant()} directive", DiagnosticSeverity.Warning);
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
                    {
                        uint v = (uint)val.Value;
                        section.EmitByte((byte)(v & 0xFF));
                        section.EmitByte((byte)((v >> 8) & 0xFF));
                        section.EmitByte((byte)((v >> 16) & 0xFF));
                        section.EmitByte((byte)((v >> 24) & 0xFF));
                    }
                    else
                    {
                        section.ReserveBytes(4);
                    }
                }
                break;

            case SyntaxKind.DsKeyword:
                if (expressions.Count > 0)
                {
                    // Check for DS ALIGN[n] syntax
                    if (IsDsAlign(expressions[0]))
                    {
                        EmitDsAlign(section, evaluator, expressions);
                        break;
                    }

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
        int mask = boundary - 1;
        int pad = ((boundary - ((section.CurrentPC - alignOfs) & mask)) & mask);
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

        int boundary = 1 << (int)alignBits.Value;
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
