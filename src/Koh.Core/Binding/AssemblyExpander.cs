using System.Text.RegularExpressions;
using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;
using Koh.Core.Text;

namespace Koh.Core.Binding;

/// <summary>
/// A single effective statement after all macro/REPT/IF expansion.
/// SourceFilePath tracks which file the node came from (for INCBIN path resolution).
/// </summary>
public sealed record ExpandedNode(SyntaxNode Node, string SourceFilePath = "", bool WasInConditional = false);

/// <summary>
/// Expands macros, REPT/FOR loops, conditional assembly, and INCLUDE directives
/// into a flat list of effective statements. The Binder's Pass 1 and Pass 2 then
/// iterate this list with no block-tracking state.
/// </summary>
internal sealed class AssemblyExpander
{
    private readonly DiagnosticBag _diagnostics;
    private readonly SymbolTable _symbols;
    private readonly ConditionalAssemblyState _conditional = new();
    private readonly Dictionary<string, MacroDef> _macros = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _equsConstants = new(StringComparer.OrdinalIgnoreCase);
    private readonly CharMapManager _charMaps;
    private long _rsCounter; // RS counter for RB/RW/RSRESET/RSSET
    private readonly ISourceFileResolver _fileResolver;
    private readonly HashSet<string> _includeStack = new(StringComparer.OrdinalIgnoreCase);
    private int _uniqueIdCounter;
    private int _expansionDepth;
    private SourceText? _currentSourceText;
    private string _currentFilePath = "";
    private bool _breakRequested;
    private bool _shiftRequested; // set by SHIFT inside macro body — signals ExpandMacroBody to re-expand
    private TextWriter _printOutput;
    private int _loopDepth; // >0 means inside REPT/FOR expansion
    private readonly Stack<int> _reptUniqueIdStack = new(); // per-REPT-iteration unique IDs

    // Macro frame stack — each macro invocation pushes a frame so \N, \#, _NARG
    // resolve lazily against the current args+shift state. SHIFT mutates the top frame.
    private readonly Stack<MacroFrame> _macroFrameStack = new();

    private sealed class MacroFrame
    {
        public IReadOnlyList<string> Args { get; }
        public int ShiftOffset { get; set; }
        public int UniqueId { get; set; }
        public int Narg => Math.Max(0, Args.Count - ShiftOffset);
        public string GetArg(int oneBasedIndex)
        {
            int i = oneBasedIndex - 1 + ShiftOffset;
            return i >= 0 && i < Args.Count ? Args[i] : "";
        }
        public string AllArgs()
        {
            var remaining = new List<string>();
            for (int i = ShiftOffset; i < Args.Count; i++)
                remaining.Add(Args[i]);
            return string.Join(", ", remaining);
        }
        public MacroFrame(IReadOnlyList<string> args) => Args = args;
    }

    private const int MaxExpansionDepth = 64;

    public AssemblyExpander(DiagnosticBag diagnostics, SymbolTable symbols,
        ISourceFileResolver? fileResolver = null, CharMapManager? charMaps = null,
        TextWriter? printOutput = null)
    {
        _diagnostics = diagnostics;
        _symbols = symbols;
        _fileResolver = fileResolver ?? new FileSystemResolver();
        _charMaps = charMaps ?? new CharMapManager(diagnostics);
        _printOutput = printOutput ?? Console.Error;
    }

    /// <summary>The expander's charmap state, for sharing with the binder.</summary>
    internal CharMapManager CharMaps => _charMaps;

    /// <summary>Optional callback to retrieve the current PC for @ interpolation.</summary>
    internal Func<int>? GetCurrentPC { get; set; }

    /// <summary>Look up an EQUS constant by name. Returns null if not found.</summary>
    internal string? LookupEqus(string name) =>
        _equsConstants.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Optional callback to resolve SECTION(@) and SECTION(label) in string interpolation.
    /// </summary>
    internal Func<string, string?>? SectionNameResolver { get; set; }

    public List<ExpandedNode> Expand(SyntaxTree tree)
    {
        var output = new List<ExpandedNode>();
        _currentSourceText = tree.Text;
        _currentFilePath = tree.Text.FilePath;
        _diagnostics.CurrentFilePath = _currentFilePath;
        // Seed include stack with root file for circular detection
        if (!string.IsNullOrEmpty(_currentFilePath))
            _includeStack.Add(_currentFilePath);
        var children = tree.Root.ChildNodesAndTokens().ToList();

        // Pre-scan: define all EQU/SET/DEF constants in the root node list before the main
        // expansion walk. This makes forward-declared constants visible to DS count expressions
        // in Pass 1, preventing a size divergence between Pass 1 and Pass 2.
        // Constants whose values depend on other forward-declared constants are resolved in
        // up to two passes of the pre-scan; circular definitions are silently skipped (they
        // will produce an "undefined symbol" diagnostic during evaluation).
        PreScanEquConstants(children);

        int i = 0;
        ExpandBodyList(children, ref i, output);

        if (_conditional.HasUnclosedBlocks)
            _diagnostics.Report(default, "Unclosed IF block: missing ENDC");

        return output;
    }

    /// <summary>
    /// Pre-scan the top-level child list for EQU/SET/DEF/EQUS/RS constant definitions and
    /// register them before the main expansion walk begins. This ensures that DS directives
    /// referencing forward-declared constants can resolve their counts during Pass 1.
    ///
    /// Runs in a convergence loop: each pass resolves constants whose dependencies were
    /// defined in the previous pass. Stops when no new constants are resolved or after
    /// 8 iterations (handles arbitrarily deep chains like A EQU B, B EQU C, C EQU 5).
    /// </summary>
    private void PreScanEquConstants(IReadOnlyList<SyntaxNodeOrToken> siblings)
    {
        const int maxPasses = 8;
        int previousCount = _symbols.DefinedCount;

        for (int pass = 0; pass < maxPasses; pass++)
        {
            foreach (var item in siblings)
            {
                if (!item.IsNode) continue;
                var node = item.AsNode!;
                if (node.Kind == SyntaxKind.SymbolDirective)
                    EarlyDefineEquNoError(node);
            }

            int currentCount = _symbols.DefinedCount;
            if (currentCount == previousCount)
                break; // no new constants resolved — converged
            previousCount = currentCount;
        }
    }

    /// <summary>
    /// Like EarlyDefineEqu but silently skips when the value expression cannot be resolved
    /// (instead of producing a diagnostic). Used during pre-scan where unresolved forward
    /// references are expected — they are retried on the second pass or during the normal walk.
    /// </summary>
    private void EarlyDefineEquNoError(SyntaxNode node)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count < 2) return;

        int nameIdx = 0;
        if (tokens[0].Kind is SyntaxKind.DefKeyword or SyntaxKind.RedefKeyword)
            nameIdx = 1;

        if (nameIdx + 1 >= tokens.Count) return;
        if (tokens[nameIdx].Kind != SyntaxKind.IdentifierToken) return;

        var kwKind = tokens[nameIdx + 1].Kind;
        if (kwKind is SyntaxKind.EquKeyword or SyntaxKind.EqualsToken)
        {
            var exprNodes = node.ChildNodes().ToList();
            if (exprNodes.Count == 0) return;
            // Skip char literals in the pre-scan: their values depend on charmap state which is
            // not fully known until the sequential expansion walk processes all CHARMAP directives.
            // Allowing the pre-scan to define them with the wrong (ASCII) value would conflict
            // with the later correct (charmap-mapped) definition from EarlyDefineEqu.
            if (ContainsCharLiteral(exprNodes[0].Green)) return;
            // Use a silent evaluator — don't report missing-symbol errors during pre-scan
            var evaluator = new ExpressionEvaluator(_symbols, DiagnosticBag.Null, () => 0, 0, _charMaps);
            var value = evaluator.TryEvaluate(exprNodes[0].Green);
            if (!value.HasValue) return; // unresolvable at this point — retry on next pass
            if (kwKind == SyntaxKind.EqualsToken || tokens[0].Kind == SyntaxKind.RedefKeyword)
                _symbols.DefineOrRedefine(tokens[nameIdx].Text, value.Value);
            else
                // EQU: only define if not already defined (avoid duplicate-definition diagnostic)
                _symbols.DefineConstantIfAbsent(tokens[nameIdx].Text, value.Value, node);
        }
        else if (kwKind is SyntaxKind.RbKeyword or SyntaxKind.RwKeyword or SyntaxKind.RlKeyword)
        {
            // RS allocation — skip in pre-scan; RS counters depend on declaration order and
            // are handled correctly by EarlyDefineEqu during the sequential expansion walk.
        }
        // EQUS pre-scan is skipped: string constants are macro-expansion-time, not DS-count-time.
    }

    // =========================================================================
    // Core expansion kernel
    // =========================================================================

    private void ExpandBodyList(IReadOnlyList<SyntaxNodeOrToken> siblings,
        ref int i, List<ExpandedNode> output)
    {
        while (i < siblings.Count && !_breakRequested)
        {
            var item = siblings[i];
            if (!item.IsNode) { i++; continue; }
            var node = item.AsNode!;

            // Lazy macro param resolution: if inside a macro frame with SHIFT,
            // MacroParamTokens (\1..\9, \#) survive in the tree and need resolving.
            if (!_conditional.IsSuppressed && _macroFrameStack.Count > 0 && ContainsMacroParam(node.Green))
            {
                var frame = _macroFrameStack.Peek();
                var rawText = _currentSourceText != null
                    ? _currentSourceText.ToString(node.FullSpan)
                    : node.Green.ToString();
                var resolved = ResolveMacroParamsInText(rawText, frame);
                if (resolved != rawText)
                {
                    ExpandTextInline(resolved, output);
                    i++;
                    continue;
                }
            }

            // {EQUS} interpolation — DEF {prefix}banana EQU 1, PURGE {prefix}banana, etc.
            if (!_conditional.IsSuppressed && _currentSourceText != null && NeedsInterpolation(node))
            {
                var rawText = _currentSourceText.ToString(node.FullSpan);
                var resolved = ResolveInterpolations(rawText);
                if (resolved != rawText)
                {
                    ExpandTextInline(resolved, output);
                    i++;
                    continue;
                }
            }

            if (node.Kind == SyntaxKind.ConditionalDirective)
            {
                // Check for label before ENDC (same line)
                var condKw = ((GreenToken)((GreenNode)node.Green).GetChild(0)!).Kind;
                if (condKw == SyntaxKind.EndcKeyword && i > 0 && siblings[i - 1].IsNode
                    && siblings[i - 1].AsNode!.Kind == SyntaxKind.LabelDeclaration)
                {
                    _diagnostics.Report(node.FullSpan, "Label on the same line as ENDC is not allowed");
                }
                HandleConditional(node);
                i++;
                continue;
            }

            if (_conditional.IsSuppressed)
            {
                if (node.Kind == SyntaxKind.MacroDefinition)
                    SkipMacroBlock(siblings, ref i);
                else if (node.Kind == SyntaxKind.RepeatDirective)
                    SkipRepeatBlock(siblings, ref i);
                else
                    i++;
                continue;
            }

            if (node.Kind == SyntaxKind.MacroDefinition)
            {
                var kw = node.ChildTokens().FirstOrDefault();
                // Check for trailing tokens after ENDM
                if (kw?.Kind == SyntaxKind.EndmKeyword)
                {
                    var allToks = node.ChildTokens().ToList();
                    if (allToks.Count > 1)
                        _diagnostics.Report(node.FullSpan, "Unexpected tokens after ENDM");
                }
                if (kw?.Kind == SyntaxKind.MacroKeyword)
                {
                    // Try old syntax: label preceding MACRO keyword
                    string? macroName = PeekMacroName(siblings, i);
                    // Try RGBDS 0.5+ syntax: MACRO name (name as token after keyword)
                    if (macroName == null)
                    {
                        var nameToken = node.ChildTokens()
                            .FirstOrDefault(t => t.Kind == SyntaxKind.IdentifierToken);
                        macroName = nameToken?.Text;
                    }
                    if (macroName != null)
                        CollectMacroBody(siblings, ref i, macroName);
                    else
                    {
                        _diagnostics.Report(node.FullSpan, "MACRO requires a name");
                        SkipMacroBlock(siblings, ref i);
                    }
                }
                else if (kw?.Kind == SyntaxKind.EndmKeyword)
                {
                    _diagnostics.Report(node.FullSpan, "ENDM without matching MACRO");
                    i++;
                }
                else
                    i++;
                continue;
            }

            if (node.Kind == SyntaxKind.RepeatDirective)
            {
                var kw = node.ChildTokens().FirstOrDefault();
                // Check for trailing tokens after ENDR
                if (kw?.Kind == SyntaxKind.EndrKeyword)
                {
                    var allToks = node.ChildTokens().ToList();
                    if (allToks.Count > 1)
                        _diagnostics.Report(node.FullSpan, "Unexpected tokens after ENDR");
                }
                if (kw?.Kind == SyntaxKind.ReptKeyword)
                    ExpandRept(node, siblings, ref i, output);
                else if (kw?.Kind == SyntaxKind.ForKeyword)
                    ExpandFor(node, siblings, ref i, output);
                else if (kw?.Kind == SyntaxKind.EndrKeyword)
                {
                    _diagnostics.Report(node.FullSpan, "ENDR without matching REPT/FOR");
                    i++;
                }
                else
                    i++;
                continue;
            }

            if (node.Kind == SyntaxKind.MacroCall)
            {
                // Check if it's an EQUS bare-name expansion before macro lookup
                var callName = node.ChildTokens().FirstOrDefault()?.Text;
                if (callName != null && _equsConstants.TryGetValue(callName, out var equsValue))
                {
                    ExpandTextInline(equsValue, output);
                    i++;
                    continue;
                }
                ExpandMacroCall(node, output);
                i++;
                continue;
            }

            // INCLUDE — textual inclusion of another source file
            if (node.Kind == SyntaxKind.IncludeDirective)
            {
                var kw = node.ChildTokens().FirstOrDefault();
                if (kw?.Kind == SyntaxKind.IncludeKeyword)
                    ExpandInclude(node, output);
                else if (kw?.Kind == SyntaxKind.IncbinKeyword)
                    output.Add(new ExpandedNode(node, _currentFilePath, _conditional.Depth > 0)); // INCBIN handled by binder Pass 2
                i++;
                continue;
            }

            // Label before MACRO — skip (it's the macro name)
            if (node.Kind == SyntaxKind.LabelDeclaration && i + 1 < siblings.Count)
            {
                var next = siblings[i + 1];
                if (next.IsNode && next.AsNode!.Kind == SyntaxKind.MacroDefinition)
                {
                    var nextKw = next.AsNode!.ChildTokens().FirstOrDefault();
                    if (nextKw?.Kind == SyntaxKind.MacroKeyword)
                    {
                        i++;
                        continue;
                    }
                }
            }

            // EQU constants, RS counters, and charmaps — define immediately for IF/REPT/REVCHAR
            if (node.Kind == SyntaxKind.SymbolDirective)
            {
                // Handle PURGE during expansion — remove symbols from EQUS constants and symbol table
                var symTokens = node.ChildTokens().ToList();
                if (symTokens.Count > 0 && symTokens[0].Kind == SyntaxKind.PurgeKeyword)
                {
                    HandlePurge(node);
                    i++;
                    continue; // consumed — don't emit to output
                }
                EarlyDefineEqu(node);
                EarlyProcessCharmap(node);
            }

            // SHIFT handling — advance the macro argument window
            if (node.Kind == SyntaxKind.DirectiveStatement)
            {
                var kwCheck = node.ChildTokens().FirstOrDefault();
                if (kwCheck?.Kind == SyntaxKind.ShiftKeyword)
                {
                    if (_macroFrameStack.Count == 0)
                    {
                        _diagnostics.Report(node.FullSpan, "SHIFT used outside of a macro");
                        i++;
                        continue;
                    }
                    var frame = _macroFrameStack.Peek();
                    if (frame.ShiftOffset >= frame.Args.Count)
                        _diagnostics.Report(node.FullSpan,
                            "Cannot shift macro arguments past their end",
                            DiagnosticSeverity.Warning);
                    frame.ShiftOffset++;
                    _symbols.DefineOrRedefine("_NARG", frame.Narg);
                    i++;
                    continue;
                }
            }

            // Check for macro parameter tokens (\1..\9) outside macro context.
            // Must walk the full green subtree because \1 may be inside a nested expression node
            // (e.g. "println \1" → DirectiveStatement[PrintlnKw, LiteralExpression[MacroParamToken]]).
            if (_expansionDepth == 0)
            {
                var macroParam = FindMacroParamToken(node.Green);
                if (macroParam != null)
                    _diagnostics.Report(node.FullSpan, $"Macro argument {macroParam} used outside of a macro");
            }

            // RSRESET/RSSET — handle RS counter directives during expansion
            // BREAK — signal loop exit
            if (node.Kind == SyntaxKind.DirectiveStatement)
            {
                var kw = node.ChildTokens().FirstOrDefault();
                if (kw?.Kind is SyntaxKind.RsresetKeyword or SyntaxKind.RssetKeyword)
                {
                    HandleRsDirective(node);
                    i++;
                    continue; // consumed — don't emit to output
                }
                if (kw?.Kind == SyntaxKind.BreakKeyword)
                {
                    _breakRequested = true;
                    i++;
                    return; // exit ExpandBodyList — caller loop will see the flag
                }
                // PRINT/PRINTLN inside loops — resolve at expansion time so per-iteration output is correct
                if (_loopDepth > 0 && kw?.Kind is SyntaxKind.PrintKeyword or SyntaxKind.PrintlnKeyword)
                {
                    var msgToken = node.ChildTokens()
                        .FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
                    if (msgToken != null)
                    {
                        var text = msgToken.Text.Length >= 2 ? msgToken.Text[1..^1] : msgToken.Text;
                        text = ResolveInterpolations(text);
                        _printOutput.Write(text);
                    }
                    if (kw.Kind == SyntaxKind.PrintlnKeyword)
                        _printOutput.WriteLine();
                    i++;
                    continue; // consumed — don't emit to output (already printed)
                }
            }

            output.Add(new ExpandedNode(node, _currentFilePath, _conditional.Depth > 0));
            i++;
        }
    }

    private void EarlyDefineEqu(SyntaxNode node)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count < 2) return;

        // Handle DEF/REDEF prefix: skip to the identifier + keyword pair
        int nameIdx = 0;
        if (tokens[0].Kind is SyntaxKind.DefKeyword or SyntaxKind.RedefKeyword)
            nameIdx = 1;

        if (nameIdx + 1 >= tokens.Count) return;

        if (tokens[nameIdx].Kind == SyntaxKind.IdentifierToken &&
            tokens[nameIdx + 1].Kind is SyntaxKind.EquKeyword or SyntaxKind.EqualsToken)
        {
            // Check for built-in symbol protection
            if (tokens[0].Kind == SyntaxKind.RedefKeyword && BuiltinSymbols.Contains(tokens[nameIdx].Text))
            {
                _diagnostics.Report(node.FullSpan, $"Cannot redefine built-in symbol '{tokens[nameIdx].Text}'");
                return;
            }

            var exprNodes = node.ChildNodes().ToList();
            if (exprNodes.Count > 0)
            {
                var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps, ResolveInterpolations);
                var value = evaluator.TryEvaluate(exprNodes[0].Green);
                if (value.HasValue)
                {
                    // = and REDEF are reassignable (SET semantics); EQU is immutable.
                    if (tokens[nameIdx + 1].Kind == SyntaxKind.EqualsToken ||
                        tokens[0].Kind == SyntaxKind.RedefKeyword)
                        _symbols.DefineOrRedefine(tokens[nameIdx].Text, value.Value);
                    else
                        _symbols.DefineConstant(tokens[nameIdx].Text, value.Value, node);
                }
            }
        }
        else if (tokens[nameIdx].Kind == SyntaxKind.IdentifierToken &&
                 tokens[nameIdx + 1].Kind is SyntaxKind.RbKeyword or SyntaxKind.RwKeyword or SyntaxKind.RlKeyword)
        {
            int multiplier = tokens[nameIdx + 1].Kind switch
            {
                SyntaxKind.RwKeyword => 2,
                SyntaxKind.RlKeyword => 4,
                _ => 1,
            };
            long count = 1; // default count is 1

            var exprNodes = node.ChildNodes().ToList();
            if (exprNodes.Count > 0)
            {
                var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps);
                var value = evaluator.TryEvaluate(exprNodes[0].Green);
                if (value.HasValue) count = value.Value;
            }

            _symbols.DefineConstant(tokens[nameIdx].Text, _rsCounter, node);
            _rsCounter += count * multiplier;
        }
        else if (tokens[nameIdx].Kind == SyntaxKind.IdentifierToken &&
                 tokens[nameIdx + 1].Kind == SyntaxKind.EqusKeyword)
        {
            // name EQUS expr — evaluate string expression
            var exprNodes = node.ChildNodes().ToList();
            if (exprNodes.Count > 0)
            {
                var value = EvaluateStringExpression(exprNodes[0]);
                if (value != null)
                    _equsConstants[tokens[nameIdx].Text] = value;
            }
        }
    }

    /// <summary>
    /// Evaluate a string expression for EQUS assignment. Handles string literals, REVCHAR(),
    /// and all string-returning functions (STRUPR, STRLWR, STRCAT, READFILE, etc.).
    /// </summary>
    private string? EvaluateStringExpression(SyntaxNode exprNode)
    {
        // Use the full ExpressionEvaluator with string support
        var evaluator = CreateStringEvaluator();
        var result = evaluator.TryEvaluateString(exprNode.Green);
        if (result != null) return result;

        // ++ concatenation (BinaryExpression)
        if (exprNode.Kind == SyntaxKind.BinaryExpression)
        {
            var childTokens = exprNode.ChildTokens().ToList();
            if (childTokens.Any(t => t.Kind == SyntaxKind.PlusPlusToken))
            {
                var children = exprNode.ChildNodes().ToList();
                if (children.Count >= 2)
                {
                    var left = EvaluateStringExpression(children[0]);
                    var right = EvaluateStringExpression(children[^1]);
                    if (left != null && right != null)
                        return left + right;
                }
            }
        }

        // Function calls
        if (exprNode.Kind == SyntaxKind.FunctionCallExpression)
        {
            var funcTokens = exprNode.ChildTokens().ToList();
            if (funcTokens.Count == 0) return null;
            var funcKind = funcTokens[0].Kind;
            var argExprs = exprNode.ChildNodes().ToList();

            switch (funcKind)
            {
                case SyntaxKind.RevcharKeyword:
                {
                    var numEval = new ExpressionEvaluator(_symbols, _diagnostics, () => 0);
                    var bytes = new List<byte>();
                    foreach (var argExpr in argExprs)
                    {
                        var val = numEval.TryEvaluate(argExpr.Green);
                        if (val.HasValue)
                            bytes.Add((byte)(val.Value & 0xFF));
                    }
                    if (bytes.Count > 0)
                        return _charMaps.ReverseCharMap(bytes.ToArray());
                    break;
                }

                case SyntaxKind.StrcharKeyword:
                {
                    if (argExprs.Count >= 2)
                    {
                        var s = EvaluateStringExpression(argExprs[0]);
                        var numEval = new ExpressionEvaluator(_symbols, _diagnostics, () => 0);
                        var idxVal = numEval.TryEvaluate(argExprs[1].Green);
                        if (s != null && idxVal.HasValue)
                        {
                            int idx = (int)idxVal.Value;
                            if (idx >= 1 && idx <= s.Length)
                                return s[(idx - 1)..idx];
                        }
                    }
                    break;
                }
                case SyntaxKind.StruprKeyword:
                {
                    if (argExprs.Count > 0)
                    {
                        var s = EvaluateStringExpression(argExprs[0]);
                        return s?.ToUpperInvariant();
                    }
                    break;
                }
                case SyntaxKind.StrlwrKeyword:
                {
                    if (argExprs.Count > 0)
                    {
                        var s = EvaluateStringExpression(argExprs[0]);
                        return s?.ToLowerInvariant();
                    }
                    break;
                }
                case SyntaxKind.StrcatKeyword:
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var argExpr in argExprs)
                    {
                        var s = EvaluateStringExpression(argExpr);
                        if (s == null) return null;
                        sb.Append(s);
                    }
                    return sb.ToString();
                }
                case SyntaxKind.StrsubKeyword:
                {
                    if (argExprs.Count >= 2)
                    {
                        var s = EvaluateStringExpression(argExprs[0]);
                        if (s == null) return null;
                        var numEval2 = new ExpressionEvaluator(_symbols, _diagnostics, () => 0);
                        var startVal = numEval2.TryEvaluate(argExprs[1].Green);
                        if (!startVal.HasValue) return null;
                        int start = (int)startVal.Value - 1; // 1-based
                        int len = s.Length - start;
                        if (argExprs.Count >= 3)
                        {
                            var lenVal = numEval2.TryEvaluate(argExprs[2].Green);
                            if (lenVal.HasValue) len = (int)lenVal.Value;
                        }
                        if (start < 0) start = 0;
                        if (start + len > s.Length) len = s.Length - start;
                        if (len < 0) len = 0;
                        return s.Substring(start, len);
                    }
                    break;
                }
                case SyntaxKind.ReadfileKeyword:
                {
                    if (argExprs.Count > 0)
                    {
                        var filename = EvaluateStringExpression(argExprs[0]);
                        if (filename == null) return null;
                        var resolved = _fileResolver.ResolvePath(_currentFilePath, filename);
                        if (!_fileResolver.FileExists(resolved)) return null;
                        try
                        {
                            var content = _fileResolver.ReadAllText(resolved);
                            if (argExprs.Count > 1)
                            {
                                var numEval3 = new ExpressionEvaluator(_symbols, _diagnostics, () => 0);
                                var limit = numEval3.TryEvaluate(argExprs[1].Green);
                                if (limit.HasValue && limit.Value < content.Length)
                                    content = content[..(int)limit.Value];
                            }
                            return content;
                        }
                        catch { return null; }
                    }
                    break;
                }
            }
        }

        // Name expression referencing an EQUS constant
        if (exprNode.Kind == SyntaxKind.NameExpression)
        {
            var nameToken = exprNode.ChildTokens().FirstOrDefault();
            if (nameToken != null)
            {
                var name = nameToken.Text;
                if (name.StartsWith('#'))
                    name = name[1..];
                if (_equsConstants.TryGetValue(name, out var equsVal))
                    return equsVal;
            }
        }

        return null;
    }

    /// <summary>
    /// Create an ExpressionEvaluator configured for string evaluation in the expander context.
    /// </summary>
    private ExpressionEvaluator CreateStringEvaluator()
    {
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0)
        {
            EqusResolver = LookupEqus,
            CharlenResolver = s => _charMaps.CharLen(s),
            IncharmapResolver = s => _charMaps.InCharMap(s),
            ReadfileResolver = ResolveReadfile,
        };
        return evaluator;
    }

    /// <summary>
    /// Shared file reading logic for READFILE function.
    /// </summary>
    internal string? ResolveReadfile(string path, int? limit)
    {
        var resolved = _fileResolver.ResolvePath(_currentFilePath, path);
        if (!_fileResolver.FileExists(resolved))
        {
            _diagnostics.Report(default, $"READFILE: file not found: {path}");
            return null;
        }
        try
        {
            var text = _fileResolver.ReadAllText(resolved);
            if (limit.HasValue && limit.Value < text.Length)
                text = text.Substring(0, limit.Value);
            return text;
        }
        catch (IOException ex)
        {
            _diagnostics.Report(default, $"READFILE: cannot read '{path}': {ex.Message}");
            return null;
        }
    }

    private void EarlyProcessCharmap(SyntaxNode node)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count == 0) return;

        switch (tokens[0].Kind)
        {
            case SyntaxKind.NewcharmapKeyword:
                if (tokens.Count >= 2)
                {
                    var name = StripQuotes(tokens[1].Text);
                    string? baseName = null;
                    for (int ci = 2; ci < tokens.Count; ci++)
                    {
                        if (tokens[ci].Kind is SyntaxKind.IdentifierToken or SyntaxKind.StringLiteral)
                        {
                            baseName = StripQuotes(tokens[ci].Text);
                            break;
                        }
                    }
                    _charMaps.NewCharMap(name, baseName);
                }
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
                    // Collect all number literal tokens after the string as multi-byte value
                    var byteValues = new List<byte>();
                    for (int ci = 2; ci < tokens.Count; ci++)
                    {
                        if (tokens[ci].Kind == SyntaxKind.NumberLiteral)
                        {
                            var val = ExpressionEvaluator.ParseNumber(tokens[ci].Text);
                            if (val.HasValue)
                            {
                                if (val.Value > 0xFF)
                                    _diagnostics.Report(default,
                                        $"CHARMAP value ${val.Value:X} truncated to ${val.Value & 0xFF:X2}",
                                        Diagnostics.DiagnosticSeverity.Warning);
                                byteValues.Add((byte)(val.Value & 0xFF));
                            }
                        }
                    }
                    if (byteValues.Count > 0)
                        _charMaps.AddMapping(charStr, byteValues.ToArray());
                }
                break;
        }
    }

    private static string StripQuotes(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    /// <summary>
    /// Process escape sequences in a string: \n → newline, \" → ", \\ → \, \t → tab.
    /// Used for EQUS string values which may contain embedded newlines and escaped quotes.
    /// </summary>
    private static string UnescapeString(string text)
    {
        if (!text.Contains('\\')) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                switch (text[i + 1])
                {
                    case 'n': sb.Append('\n'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    case '"': sb.Append('"'); i++; break;
                    case '0': sb.Append('\0'); i++; break;
                    default: sb.Append(text[i]); break;
                }
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Handle PURGE directive — remove symbols from both EQUS constants and the symbol table.
    /// Resolves {EQUS} interpolations in purge target names.
    /// </summary>
    private static readonly HashSet<string> BuiltinSymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        "__RGBDS_MAJOR__", "__RGBDS_MINOR__", "__RGBDS_PATCH__", "__RGBDS_RC__",
        "__UTC_YEAR__", "__UTC_MONTH__", "__UTC_DAY__",
        "__UTC_HOUR__", "__UTC_MINUTE__", "__UTC_SECOND__",
        "__ISO_8601_UTC__", "__ISO_8601_LOCAL__",
    };

    private void HandlePurge(SyntaxNode node)
    {
        var tokens = node.ChildTokens().ToList();
        for (int ti = 1; ti < tokens.Count; ti++)
        {
            if (tokens[ti].Kind == SyntaxKind.CommaToken) continue;
            if (tokens[ti].Kind is SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken)
            {
                var name = tokens[ti].Text;
                // Check for built-in symbol protection
                if (BuiltinSymbols.Contains(name))
                {
                    _diagnostics.Report(node.FullSpan, $"Cannot PURGE built-in symbol '{name}'");
                    continue;
                }
                // Check for purge of already-undefined symbol
                var sym = _symbols.Lookup(name);
                if (sym == null && !_equsConstants.ContainsKey(name))
                {
                    _diagnostics.Report(node.FullSpan, $"PURGE: symbol '{name}' is not defined");
                    continue;
                }
                // Check for purge of referenced symbol (has references)
                if (sym != null && sym.HasReferences)
                {
                    _diagnostics.Report(node.FullSpan, $"Cannot PURGE symbol '{name}' that has been referenced");
                    continue;
                }
                _equsConstants.Remove(name);
                _symbols.Remove(name);
            }
        }
    }

    private void HandleRsDirective(SyntaxNode node)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count == 0) return;

        switch (tokens[0].Kind)
        {
            case SyntaxKind.RsresetKeyword:
                _rsCounter = 0;
                break;
            case SyntaxKind.RssetKeyword:
            {
                var exprNodes = node.ChildNodes().ToList();
                if (exprNodes.Count > 0)
                {
                    var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps);
                    var value = evaluator.TryEvaluate(exprNodes[0].Green);
                    if (value.HasValue) _rsCounter = value.Value;
                }
                else
                {
                    _diagnostics.Report(node.FullSpan, "RSSET requires a value");
                }
                break;
            }
        }
    }

    // =========================================================================
    // Conditional assembly
    // =========================================================================

    private void HandleConditional(SyntaxNode node)
    {
        var keyword = ((GreenToken)((GreenNode)node.Green).GetChild(0)!).Kind;

        // Check for trailing tokens after ENDC or ELSE
        if (keyword is SyntaxKind.EndcKeyword or SyntaxKind.ElseKeyword)
        {
            var allToks = node.ChildTokens().ToList();
            if (allToks.Count > 1)
                _diagnostics.Report(node.FullSpan, $"Unexpected tokens after {(keyword == SyntaxKind.EndcKeyword ? "ENDC" : "ELSE")}");
        }

        // Check for label before ENDC (label on same line as ENDC)
        // This is detected by looking at preceding sibling nodes — handled in ExpandBodyList

        bool Eval()
        {
            var exprNode = node.ChildNodes().FirstOrDefault();
            if (exprNode == null) return false;
            var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps, ResolveInterpolations);
            return evaluator.TryEvaluate(exprNode.Green) is { } v && v != 0;
        }

        switch (keyword)
        {
            case SyntaxKind.IfKeyword:
                _conditional.HandleIf(Eval);
                break;
            case SyntaxKind.ElifKeyword:
                if (_conditional.HasSeenElse)
                    _diagnostics.Report(node.FullSpan, "ELIF after ELSE");
                else if (!_conditional.HandleElif(Eval))
                    _diagnostics.Report(node.FullSpan, "ELIF without matching IF");
                break;
            case SyntaxKind.ElseKeyword:
            {
                var result = _conditional.HandleElseEx();
                if (result == 0)
                    _diagnostics.Report(node.FullSpan, "ELSE without matching IF");
                else if (result == 2)
                    _diagnostics.Report(node.FullSpan, "Multiple ELSE blocks in one IF");
                break;
            }
            case SyntaxKind.EndcKeyword:
                if (!_conditional.HandleEndc())
                    _diagnostics.Report(node.FullSpan, "ENDC without matching IF");
                break;
        }
    }

    // =========================================================================
    // Macro collection and expansion
    // =========================================================================

    private string? PeekMacroName(IReadOnlyList<SyntaxNodeOrToken> siblings, int macroIndex)
    {
        if (macroIndex > 0 && siblings[macroIndex - 1].IsNode &&
            siblings[macroIndex - 1].AsNode!.Kind == SyntaxKind.LabelDeclaration)
            return siblings[macroIndex - 1].AsNode!.ChildTokens().First().Text;
        return null;
    }

    private void CollectMacroBody(IReadOnlyList<SyntaxNodeOrToken> siblings,
        ref int i, string macroName)
    {
        var macroNode = siblings[i].AsNode!;
        int bodyStart = macroNode.FullSpan.Start + macroNode.FullSpan.Length;
        i++;

        int depth = 1;
        int bodyEnd = bodyStart;

        while (i < siblings.Count)
        {
            if (!siblings[i].IsNode) { i++; continue; }
            var candidate = siblings[i].AsNode!;
            if (candidate.Kind == SyntaxKind.MacroDefinition)
            {
                var kw = candidate.ChildTokens().FirstOrDefault();
                if (kw?.Kind == SyntaxKind.MacroKeyword) { depth++; i++; continue; }
                if (kw?.Kind == SyntaxKind.EndmKeyword)
                {
                    depth--;
                    if (depth == 0)
                    {
                        bodyEnd = candidate.Position;
                        i++;
                        break;
                    }
                    i++; continue;
                }
            }
            i++;
        }

        if (depth != 0)
        {
            _diagnostics.Report(default, $"Macro '{macroName}' has no matching ENDM");
            return;
        }

        if (_currentSourceText != null)
        {
            var bodyText = _currentSourceText.ToString(new TextSpan(bodyStart, bodyEnd - bodyStart)).Trim();
            _macros[macroName] = new MacroDef(macroName, bodyText);
        }
    }

    /// <summary>
    /// Collect macro arguments with paren-depth tracking and \, escape support.
    /// Commas inside parentheses don't split: `BANK(x), y` → 2 args, not 3.
    /// `\,` produces a literal comma in the argument.
    /// </summary>
    private static List<string> CollectMacroArgs(IReadOnlyList<SyntaxToken> tokens, int startIndex)
    {
        var args = new List<string>();
        var currentArg = new List<string>();
        int parenDepth = 0;
        bool angleBracketQuoted = false;

        for (int t = startIndex; t < tokens.Count; t++)
        {
            var tok = tokens[t];

            // Angle-bracket quoting: <arg with, commas> treated as single arg
            if (tok.Kind == SyntaxKind.LessThanToken && parenDepth == 0 && !angleBracketQuoted
                && currentArg.Count == 0)
            {
                angleBracketQuoted = true;
                continue; // skip the opening <
            }
            if (tok.Kind == SyntaxKind.GreaterThanToken && angleBracketQuoted)
            {
                angleBracketQuoted = false;
                continue; // skip the closing >
            }

            if (tok.Kind == SyntaxKind.OpenParenToken)
            {
                parenDepth++;
                currentArg.Add(tok.Text);
            }
            else if (tok.Kind == SyntaxKind.CloseParenToken)
            {
                parenDepth = Math.Max(0, parenDepth - 1);
                currentArg.Add(tok.Text);
            }
            else if (tok.Kind == SyntaxKind.CommaToken && parenDepth == 0 && !angleBracketQuoted)
            {
                args.Add(string.Join(" ", currentArg));
                currentArg.Clear();
            }
            else if (tok.Kind == SyntaxKind.MacroParamToken && tok.Text == "\\,")
            {
                currentArg.Add(",");
            }
            else
            {
                currentArg.Add(tok.Text);
            }
        }

        if (currentArg.Count > 0)
            args.Add(string.Join(" ", currentArg));

        // Trailing comma — remove the last empty argument if it was produced by a trailing comma
        if (args.Count > 0 && string.IsNullOrWhiteSpace(args[^1]))
            args.RemoveAt(args.Count - 1);

        // Trim whitespace from all arguments (RGBDS behavior)
        for (int a = 0; a < args.Count; a++)
            args[a] = args[a].Trim();

        return args;
    }

    private void ExpandMacroCall(SyntaxNode node, List<ExpandedNode> output)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count == 0) return;

        // If the call text contains \# (MacroParamToken), resolve it against
        // the parent macro frame before looking up the macro name and args.
        bool hasBackslashHash = tokens.Any(t => t.Kind == SyntaxKind.MacroParamToken && t.Text == "\\#");
        if (hasBackslashHash && _macroFrameStack.Count > 0)
        {
            var parentFrame = _macroFrameStack.Peek();
            var rawText = _currentSourceText != null
                ? _currentSourceText.ToString(node.FullSpan)
                : string.Join("", tokens.Select(t => t.Text));
            var resolved = rawText.Replace("\\#", parentFrame.AllArgs());
            ExpandTextInline(resolved, output);
            return;
        }

        var name = tokens[0].Text;
        if (!_macros.TryGetValue(name, out var macro))
        {
            _diagnostics.Report(node.FullSpan, $"Unexpected identifier '{name}'");
            return;
        }

        // Recursion depth check
        _expansionDepth++;
        if (_expansionDepth > MaxExpansionDepth)
        {
            _diagnostics.Report(node.FullSpan,
                $"Maximum macro expansion depth ({MaxExpansionDepth}) exceeded");
            _expansionDepth--;
            return;
        }

        var args = CollectMacroArgs(tokens, startIndex: 1);
        _uniqueIdCounter++;
        var frame = new MacroFrame(args) { UniqueId = _uniqueIdCounter };
        _macroFrameStack.Push(frame);

        var prevNarg = _symbols.Lookup("_NARG");
        long? savedNarg = prevNarg?.State == SymbolState.Defined ? prevNarg.Value : null;
        _symbols.DefineOrRedefine("_NARG", frame.Narg);

        try
        {
            ExpandMacroBody(macro.Body, frame, output);
        }
        finally
        {
            _macroFrameStack.Pop();
            if (savedNarg.HasValue)
                _symbols.DefineOrRedefine("_NARG", savedNarg.Value);
            _expansionDepth--;
        }
    }

    /// <summary>
    /// Expand a macro body. Substitutes eager params (\@) then parses and expands.
    /// \1..\9, \#, _NARG are either pre-substituted (no SHIFT in body) or left for
    /// lazy resolution via MacroParamToken handling in ExpandBodyList (when SHIFT is used).
    /// </summary>
    private void ExpandMacroBody(string rawBody, MacroFrame frame, List<ExpandedNode> output)
    {
        var body = SubstituteMacroParams(rawBody, frame);
        ExpandTextInline(body, output);
    }

    private string SubstituteLineParams(string line, MacroFrame frame)
    {
        line = line.Replace("\\@", $"_{frame.UniqueId}");

        for (int p = 9; p >= 1; p--)
            line = line.Replace($"\\{p}", frame.GetArg(p));

        line = ResolveComputedArgs(line, frame);

        if (line.Contains("\\#"))
            line = line.Replace("\\#", frame.AllArgs());

        line = SubstituteOutsideStrings(line, NargPattern, frame.Narg.ToString());

        return line;
    }

    // =========================================================================
    // INCLUDE — textual file inclusion
    // =========================================================================

    private void ExpandInclude(SyntaxNode node, List<ExpandedNode> output)
    {
        // Extract filename from string literal token
        var strToken = node.ChildTokens().FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
        if (strToken == null)
        {
            _diagnostics.Report(node.FullSpan, "INCLUDE requires a filename string");
            return;
        }

        var rawPath = strToken.Text;
        var filePath = rawPath.Length >= 2 ? rawPath[1..^1] : rawPath; // strip quotes

        var resolved = _fileResolver.ResolvePath(_currentFilePath, filePath);

        if (!_fileResolver.FileExists(resolved))
        {
            _diagnostics.Report(node.FullSpan, $"Included file not found: {filePath}");
            return;
        }

        // Circular include detection
        if (_includeStack.Contains(resolved))
        {
            _diagnostics.Report(node.FullSpan, $"Circular include detected: {filePath}");
            return;
        }

        _expansionDepth++;
        if (_expansionDepth > MaxExpansionDepth)
        {
            _diagnostics.Report(node.FullSpan,
                $"Maximum include depth ({MaxExpansionDepth}) exceeded");
            _expansionDepth--;
            return;
        }

        _includeStack.Add(resolved);

        try
        {
            var source = _fileResolver.ReadAllText(resolved);
            var includeText = Text.SourceText.From(source, resolved);
            var includeTree = Syntax.SyntaxTree.Parse(includeText);

            var savedText = _currentSourceText;
            var savedPath = _currentFilePath;
            _currentSourceText = includeText;
            _currentFilePath = resolved;
            _diagnostics.CurrentFilePath = resolved;

            try
            {
                var children = includeTree.Root.ChildNodesAndTokens().ToList();
                int j = 0;
                ExpandBodyList(children, ref j, output);
            }
            finally
            {
                _currentSourceText = savedText;
                _currentFilePath = savedPath;
                _diagnostics.CurrentFilePath = savedPath;
            }
        }
        catch (IOException ex)
        {
            _diagnostics.Report(node.FullSpan, $"Cannot read included file '{filePath}': {ex.Message}");
        }
        finally
        {
            _includeStack.Remove(resolved);
            _expansionDepth--;
        }
    }

    /// <summary>
    /// Check if a node contains {name} interpolation patterns that need resolving.
    /// This detects BadToken '{' in the node's children.
    /// </summary>
    private bool NeedsInterpolation(SyntaxNode node)
    {
        foreach (var tok in node.ChildTokens())
        {
            if (tok.Kind == SyntaxKind.BadToken && tok.Text == "{")
                return true;
        }
        // Also check child nodes recursively (for nested structures)
        foreach (var child in node.ChildNodes())
        {
            if (NeedsInterpolation(child))
                return true;
        }
        return false;
    }

    // Pre-compiled pattern that matches either a double-quoted string literal or any
    // non-string-literal content (captured separately). Used by SubstituteOutsideStrings.
    private static readonly Regex StringLiteralSplitter =
        new Regex(@"""(?:[^""\\]|\\.)*""|[^""]+", RegexOptions.Compiled);

    /// <summary>
    /// Replace all occurrences of <paramref name="pattern"/> in <paramref name="text"/>
    /// with <paramref name="replacement"/>, but only in regions that are not inside
    /// double-quoted string literals. String literal content is preserved verbatim.
    /// </summary>
    private static string SubstituteOutsideStrings(string text, Regex pattern, string replacement)
    {
        if (!text.Contains('"'))
            return pattern.Replace(text, replacement); // fast path: no string literals

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (Match m in StringLiteralSplitter.Matches(text))
        {
            var segment = m.Value;
            // If the segment starts with '"' it is a string literal — copy verbatim.
            if (segment.Length > 0 && segment[0] == '"')
                sb.Append(segment);
            else
                sb.Append(pattern.Replace(segment, replacement));
        }
        return sb.ToString();
    }

    private static readonly Regex NargPattern =
        new Regex(@"\b_NARG\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Substitute macro parameter tokens (\1..\9, \@, \#, _NARG) in body text.
    ///
    /// _NARG is substituted as text (like the other parameter tokens) rather than
    /// being resolved via the symbol table. This is necessary because expressions
    /// are evaluated in Pass 2, after all expansion is complete — a symbol-table
    /// value for _NARG would reflect whatever the last macro call set it to, not
    /// the current invocation's argument count. Text substitution bakes the correct
    /// value into each iteration's re-parsed tree, matching RGBDS behavior.
    /// </summary>
    /// <summary>
    /// Substitute macro params in body text. Only \@ is eagerly baked (immutable per invocation).
    /// \1..\9, \#, _NARG are resolved lazily via MacroFrame so SHIFT can mutate the view.
    /// </summary>
    private string SubstituteMacroParams(string body, MacroFrame frame)
    {
        _uniqueIdCounter++;
        frame.UniqueId = _uniqueIdCounter;

        // \@ → unique suffix per invocation (eagerly baked — immutable)
        body = body.Replace("\\@", $"_{_uniqueIdCounter}");

        bool hasShift = body.Contains("SHIFT", StringComparison.OrdinalIgnoreCase);
        if (hasShift)
        {
            // Bodies with SHIFT leave \1..\9, \#, _NARG as-is. They survive as
            // MacroParamToken in the re-parsed tree and are resolved lazily from
            // the MacroFrame during ExpandBodyList. This allows SHIFT to mutate
            // the argument window and affect subsequent references.
            return body;
        }

        // No SHIFT — eagerly substitute everything (faster, no MacroParamToken overhead)
        for (int p = 9; p >= 1; p--)
            body = body.Replace($"\\{p}", frame.GetArg(p));

        body = ResolveComputedArgs(body, frame);

        if (body.Contains("\\#"))
            body = body.Replace("\\#", frame.AllArgs());

        body = SubstituteOutsideStrings(body, NargPattern, frame.Narg.ToString());

        return body;
    }

    /// <summary>
    /// Resolve \&lt;expr&gt; computed arg index references in macro body text.
    /// The expr is evaluated as an integer and used as a 1-based argument index.
    /// Invalid expressions produce a diagnostic.
    /// </summary>
    private string ResolveComputedArgs(string body, MacroFrame frame)
    {
        int searchFrom = 0;
        while (true)
        {
            int start = body.IndexOf("\\<", searchFrom, StringComparison.Ordinal);
            if (start < 0) break;
            int end = body.IndexOf('>', start + 2);
            if (end < 0) break;

            var exprText = body[(start + 2)..end];
            var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0);
            var tree = SyntaxTree.Parse(exprText);
            var exprNode = tree.Root.ChildNodes().FirstOrDefault();
            long? val = exprNode != null ? evaluator.TryEvaluate(exprNode.Green) : null;

            if (val.HasValue)
            {
                var replacement = frame.GetArg((int)val.Value);
                body = body[..start] + replacement + body[(end + 1)..];
                searchFrom = start + replacement.Length;
            }
            else
            {
                _diagnostics.Report(default, $"Invalid computed macro argument expression: \\<{exprText}>");
                // Leave the text as-is and advance past it
                searchFrom = end + 1;
            }
        }
        return body;
    }

    // =========================================================================
    // REPT/FOR expansion
    // =========================================================================

    private List<SyntaxNodeOrToken> CollectRepeatBody(
        IReadOnlyList<SyntaxNodeOrToken> siblings, ref int i)
    {
        i++;
        var body = new List<SyntaxNodeOrToken>();
        int depth = 1;

        while (i < siblings.Count)
        {
            if (!siblings[i].IsNode) { body.Add(siblings[i]); i++; continue; }
            var candidate = siblings[i].AsNode!;
            if (candidate.Kind == SyntaxKind.RepeatDirective)
            {
                var kw = candidate.ChildTokens().FirstOrDefault();
                if (kw?.Kind is SyntaxKind.ReptKeyword or SyntaxKind.ForKeyword)
                { depth++; body.Add(siblings[i]); i++; continue; }
                if (kw?.Kind == SyntaxKind.EndrKeyword)
                {
                    depth--;
                    if (depth == 0) { i++; return body; }
                    body.Add(siblings[i]); i++; continue;
                }
            }
            body.Add(siblings[i]); i++;
        }

        _diagnostics.Report(default, "REPT/FOR without matching ENDR");
        return body;
    }

    private string ExtractBodyText(SyntaxNode headerNode,
        IReadOnlyList<SyntaxNodeOrToken> body)
    {
        if (_currentSourceText == null || body.Count == 0)
            return "";

        int bodyTextStart = headerNode.FullSpan.Start + headerNode.FullSpan.Length;
        int bodyTextEnd = bodyTextStart;

        var last = body[^1];
        if (last.IsNode)
            bodyTextEnd = last.AsNode!.FullSpan.Start + last.AsNode!.FullSpan.Length;
        else if (last.IsToken)
            bodyTextEnd = last.AsToken!.FullSpan.Start + last.AsToken!.FullSpan.Length;

        return _currentSourceText.ToString(new TextSpan(bodyTextStart, bodyTextEnd - bodyTextStart));
    }

    private void ExpandRept(SyntaxNode reptNode,
        IReadOnlyList<SyntaxNodeOrToken> siblings, ref int i,
        List<ExpandedNode> output)
    {
        var exprNodes = reptNode.ChildNodes().ToList();
        int count = 0;
        if (exprNodes.Count > 0)
        {
            var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps);
            var val = evaluator.TryEvaluate(exprNodes[0].Green);
            if (val.HasValue) count = (int)val.Value;
        }
        if (count < 0) count = 0;

        // Extract body text for \@ substitution (REPT bodies support \@ for unique labels)
        var bodyTextRaw = ExtractBodyText(reptNode, PeekBodyNodes(siblings, i));
        CollectRepeatBody(siblings, ref i); // advances index past ENDR

        var condDepthBefore = _conditional.Depth;
        _loopDepth++;
        try
        {
            for (int iter = 0; iter < count; iter++)
            {
                _uniqueIdCounter++;
                _reptUniqueIdStack.Push(_uniqueIdCounter);
                // Substitute \@ only at the REPT level; macro calls inside will
                // get their own \@ via SubstituteMacroParams.
                var iterText = bodyTextRaw.Replace("\\@", $"_{_uniqueIdCounter}");
                ExpandTextInline(iterText, output);
                _reptUniqueIdStack.Pop();
                if (_breakRequested) { _breakRequested = false; break; }
            }
        }
        finally { _loopDepth--; }
        if (_conditional.Depth != condDepthBefore)
            _diagnostics.Report(reptNode.FullSpan, "Unbalanced IF/ENDC inside REPT body");
    }

    /// <summary>
    /// Peek at body nodes without advancing the cursor (for text extraction before CollectRepeatBody).
    /// This is an intentional double-scan: we need the raw source text span of the body in order to
    /// perform \@ substitution (which requires the text before re-parsing), but we also need the
    /// authoritative parsed body list for the node-level replay path. The two passes must agree on
    /// body boundaries; both use the same depth-tracking logic and neither includes the ENDR sentinel.
    /// For realistic body sizes (kilobytes of assembly at most) the O(n) overhead is not measurable.
    /// </summary>
    private static List<SyntaxNodeOrToken> PeekBodyNodes(
        IReadOnlyList<SyntaxNodeOrToken> siblings, int reptIndex)
    {
        var result = new List<SyntaxNodeOrToken>();
        int depth = 1;
        int k = reptIndex + 1;
        while (k < siblings.Count && depth > 0)
        {
            if (siblings[k].IsNode)
            {
                var n = siblings[k].AsNode!;
                if (n.Kind == SyntaxKind.RepeatDirective)
                {
                    var kw = n.ChildTokens().FirstOrDefault();
                    if (kw?.Kind is SyntaxKind.ReptKeyword or SyntaxKind.ForKeyword) depth++;
                    else if (kw?.Kind == SyntaxKind.EndrKeyword)
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                }
            }
            result.Add(siblings[k]);
            k++;
        }
        return result;
    }

    private void ExpandFor(SyntaxNode forNode,
        IReadOnlyList<SyntaxNodeOrToken> siblings, ref int i,
        List<ExpandedNode> output)
    {
        var exprNodes = forNode.ChildNodes().ToList();
        string? varName = null;
        if (exprNodes.Count > 0 && exprNodes[0].Kind == SyntaxKind.NameExpression)
            varName = exprNodes[0].ChildTokens().FirstOrDefault()?.Text;

        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps);
        long start = exprNodes.Count > 1 ? evaluator.TryEvaluate(exprNodes[1].Green) ?? 0 : 0;
        long stop = exprNodes.Count > 2 ? evaluator.TryEvaluate(exprNodes[2].Green) ?? 0 : 0;
        long step = exprNodes.Count > 3 ? evaluator.TryEvaluate(exprNodes[3].Green) ?? 1 : 1;

        if (step == 0)
        {
            _diagnostics.Report(forNode.FullSpan, "FOR step cannot be zero");
            step = 1;
        }

        // Warn if step direction doesn't match range direction (backwards step)
        if ((step > 0 && start > stop) || (step < 0 && start < stop))
        {
            _diagnostics.Report(forNode.FullSpan,
                "FOR has backwards step; no iterations will be performed",
                Diagnostics.DiagnosticSeverity.Warning);
        }

        // Extract body text for variable + \@ substitution
        var bodyTextRaw = ExtractBodyText(forNode, PeekBodyNodes(siblings, i));
        var body = CollectRepeatBody(siblings, ref i);

        _loopDepth++;
        try
        {
            if (varName != null)
            {
                var varPattern = new Regex($@"\b{Regex.Escape(varName)}\b");

                for (long v = start; step > 0 ? v < stop : v > stop; v += step)
                {
                    _symbols.DefineOrRedefine(varName, v);

                    _uniqueIdCounter++;
                    var iterText = SubstituteOutsideStrings(bodyTextRaw, varPattern, v.ToString());
                    iterText = iterText.Replace("\\@", $"_{_uniqueIdCounter}");
                    ExpandTextInline(iterText, output);
                    if (_breakRequested) { _breakRequested = false; break; }
                }
                // Variable retains its last value after the loop (RGBDS behavior).
            }
            else
            {
                for (long v = start; step > 0 ? v < stop : v > stop; v += step)
                {
                    _uniqueIdCounter++;
                    var iterText = bodyTextRaw.Replace("\\@", $"_{_uniqueIdCounter}");
                    ExpandTextInline(iterText, output);
                    if (_breakRequested) { _breakRequested = false; break; }
                }
            }
        }
        finally { _loopDepth--; }
    }

    // =========================================================================
    // String interpolation: {symbol}, {d:symbol}, {x:symbol}, etc.
    // =========================================================================

    /// <summary>
    /// Resolve {symbol} and {fmt:symbol} interpolations in a string.
    /// Used for PRINT/PRINTLN string arguments and EQUS expansion text.
    /// </summary>
    internal string ResolveInterpolations(string text)
    {
        if (!text.Contains('{')) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '{')
            {
                // \{ — escaped brace, literal {
                sb.Append('{');
                i += 2;
                continue;
            }

            if (text[i] == '{')
            {
                int braceStart = i;
                i++; // skip {
                // Parse optional format specifier: {fmt:name} or {name}
                string? fmt = null;
                int colonPos = text.IndexOf(':', i);
                int closePos = text.IndexOf('}', i);

                if (closePos < 0)
                {
                    // Unclosed { — emit as-is
                    sb.Append('{');
                    continue;
                }

                if (colonPos >= 0 && colonPos < closePos)
                {
                    fmt = text[i..colonPos];
                    i = colonPos + 1;
                }

                string name = text[i..closePos];
                i = closePos + 1;

                // Handle SECTION(...) as a special function in interpolation
                string trimmedName = name.Trim();
                if (fmt == null && trimmedName.StartsWith("SECTION(", StringComparison.OrdinalIgnoreCase)
                    && trimmedName.EndsWith(")"))
                {
                    string arg = trimmedName.Substring(8, trimmedName.Length - 9).Trim();
                    string? sectionName = SectionNameResolver?.Invoke(arg);
                    if (sectionName != null)
                    {
                        sb.Append(sectionName);
                        continue;
                    }
                }

                // Validate format specifier if present
                string? trimmedFmt = fmt?.Trim();
                if (trimmedFmt != null && !IsValidInterpolationFormat(trimmedFmt))
                {
                    _diagnostics.Report(default,
                        $"Invalid format specifier '{trimmedFmt}' in string interpolation");
                    sb.Append(text[braceStart..i]);
                    continue;
                }

                // Resolve the symbol
                string? resolved = ResolveInterpolationValue(trimmedName, trimmedFmt);
                if (resolved != null)
                    sb.Append(resolved);
                else
                {
                    // Unknown symbol — preserve original text
                    sb.Append(text[braceStart..i]);
                    _diagnostics.Report(default, $"Interpolation: symbol '{name.Trim()}' not found");
                }
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Current section name for SECTION(@) function resolution.</summary>
    internal string? CurrentSectionName { get; set; }

    private string? ResolveInterpolationValue(string name, string? fmt)
    {
        // Handle @ (current PC) — RGBDS defaults to uppercase hex with $ prefix
        if (name == "@" && GetCurrentPC != null)
            return FormatNumericValue(GetCurrentPC(), fmt ?? "#X");

        // SECTION() function: SECTION(@) returns current section, SECTION(label) returns label's section
        if (name.StartsWith("SECTION(", StringComparison.OrdinalIgnoreCase) && name.EndsWith(')'))
        {
            var arg = name["SECTION(".Length..^1].Trim();
            if (arg == "@")
                return CurrentSectionName ?? "";
            // SECTION(label) — look up label's section
            var labelSym = _symbols.Lookup(arg);
            if (labelSym?.Section != null)
                return labelSym.Section;
            return null;
        }

        // Check EQUS constants first (string type)
        if (_equsConstants.TryGetValue(name, out var equsValue))
            return equsValue; // EQUS always returns raw string regardless of format

        // Check numeric symbols
        var sym = _symbols.Lookup(name);
        if (sym != null && sym.State == Symbols.SymbolState.Defined)
        {
            long val = sym.Value;
            return FormatNumericValue((int)val, fmt ?? "d");
        }

        return null;
    }

    private static string FormatNumericValue(long val, string fmt)
    {
        bool hasPrefix = fmt.StartsWith('#');
        string type = hasPrefix ? fmt[1..] : fmt;

        return type switch
        {
            "d" => ((int)val).ToString(),
            "u" => ((uint)val).ToString(),
            "x" => hasPrefix ? $"${val:x}" : val.ToString("x"),
            "X" => hasPrefix ? $"${val:X}" : val.ToString("X"),
            "b" => hasPrefix ? $"%{Convert.ToString(val, 2)}" : Convert.ToString(val, 2),
            "o" => hasPrefix ? $"&{Convert.ToString(val, 8)}" : Convert.ToString(val, 8),
            _ => ((int)val).ToString(),
        };
    }

    /// <summary>
    /// Returns true if the interpolation format specifier is valid.
    /// Valid forms: [+][#][0][width]type  where type ∈ {d,u,x,X,b,o,f,s}
    /// Invalid: unknown type characters, extra characters, etc.
    /// </summary>
    private static bool IsValidInterpolationFormat(string fmt)
    {
        if (string.IsNullOrEmpty(fmt)) return true; // empty = default = valid

        int pos = 0;
        // Optional sign flag
        if (pos < fmt.Length && fmt[pos] == '+') pos++;
        // Optional prefix flag
        if (pos < fmt.Length && fmt[pos] == '#') pos++;
        // Optional zero-pad flag
        if (pos < fmt.Length && fmt[pos] == '0') pos++;
        // Optional width digits
        while (pos < fmt.Length && char.IsDigit(fmt[pos])) pos++;
        // Must have exactly one type character remaining
        if (pos >= fmt.Length) return false; // no type
        char type = fmt[pos++];
        if (pos != fmt.Length) return false; // extra chars after type
        return type is 'd' or 'u' or 'x' or 'X' or 'b' or 'o' or 'f' or 's';
    }

    /// <summary>
    /// Format a numeric value according to an RGBDS format specifier.
    /// Format: [#][0][width][type] where type is d/u/x/X/b/o/f
    /// </summary>
    internal static string FormatNumericValue(int val, string? fmt)
    {
        if (string.IsNullOrEmpty(fmt))
            return val.ToString();

        // Parse format spec: [+][#][0][width][type]
        int pos = 0;
        bool showSign = false;
        bool hasPrefix = false;
        bool zeroPad = false;
        int width = 0;

        if (pos < fmt.Length && fmt[pos] == '+')
        {
            showSign = true;
            pos++;
        }
        if (pos < fmt.Length && fmt[pos] == '#')
        {
            hasPrefix = true;
            pos++;
        }
        if (pos < fmt.Length && fmt[pos] == '0')
        {
            zeroPad = true;
            pos++;
        }
        // Parse width digits
        int widthStart = pos;
        while (pos < fmt.Length && char.IsDigit(fmt[pos]))
            pos++;
        if (pos > widthStart)
            int.TryParse(fmt.AsSpan(widthStart, pos - widthStart), out width);

        // Remaining is the type character
        string type = pos < fmt.Length ? fmt[pos..] : "d";

        string prefix = "";
        if (hasPrefix)
        {
            prefix = type switch
            {
                "x" or "X" => "$",
                "b" => "%",
                "o" => "&",
                _ => "",
            };
        }

        string signStr = "";
        if (showSign && val >= 0)
            signStr = "+";

        string formatted = type switch
        {
            "d" => val.ToString(),
            "u" => ((uint)val).ToString(),
            "x" => ((uint)val).ToString("x"),
            "X" => ((uint)val).ToString("X"),
            "b" => Convert.ToString((uint)val, 2),
            "o" => Convert.ToString((uint)val, 8),
            _ => val.ToString(),
        };

        // Apply width padding — width includes prefix and sign
        int totalExtra = prefix.Length + signStr.Length;
        if (width > 0 && formatted.Length + totalExtra < width)
        {
            char padChar = zeroPad ? '0' : ' ';
            formatted = formatted.PadLeft(width - totalExtra, padChar);
        }

        return signStr + prefix + formatted;
    }

    /// <summary>
    /// Parse and expand text inline (used for macro/REPT/FOR text-level expansion).
    /// </summary>
    private void ExpandTextInline(string text, List<ExpandedNode> output)
    {
        // Resolve {symbol} interpolations before re-parsing
        text = ResolveInterpolations(text);
        _expansionDepth++;
        if (_expansionDepth > MaxExpansionDepth)
        {
            _diagnostics.Report(default,
                $"Maximum expansion depth ({MaxExpansionDepth}) exceeded");
            _expansionDepth--;
            return;
        }

        var tree = SyntaxTree.Parse(text);
        var savedText = _currentSourceText;
        _currentSourceText = tree.Text;
        try
        {
            var children = tree.Root.ChildNodesAndTokens().ToList();
            int j = 0;
            ExpandBodyList(children, ref j, output);
        }
        finally
        {
            _currentSourceText = savedText;
            _expansionDepth--;
        }
    }

    // =========================================================================
    // Block skipping (for suppressed conditional branches)
    // =========================================================================

    private void SkipMacroBlock(IReadOnlyList<SyntaxNodeOrToken> siblings, ref int i)
    {
        i++;
        int depth = 1;
        while (i < siblings.Count && depth > 0)
        {
            if (siblings[i].IsNode)
            {
                var n = siblings[i].AsNode!;
                if (n.Kind == SyntaxKind.MacroDefinition)
                {
                    var kw = n.ChildTokens().FirstOrDefault();
                    if (kw?.Kind == SyntaxKind.MacroKeyword) depth++;
                    else if (kw?.Kind == SyntaxKind.EndmKeyword) depth--;
                }
            }
            i++;
        }
    }

    private void SkipRepeatBlock(IReadOnlyList<SyntaxNodeOrToken> siblings, ref int i)
    {
        var kw = siblings[i].AsNode!.ChildTokens().FirstOrDefault();
        if (kw?.Kind is SyntaxKind.ReptKeyword or SyntaxKind.ForKeyword)
        {
            i++;
            int depth = 1;
            while (i < siblings.Count && depth > 0)
            {
                if (siblings[i].IsNode)
                {
                    var n = siblings[i].AsNode!;
                    if (n.Kind == SyntaxKind.RepeatDirective)
                    {
                        var k = n.ChildTokens().FirstOrDefault();
                        if (k?.Kind is SyntaxKind.ReptKeyword or SyntaxKind.ForKeyword) depth++;
                        else if (k?.Kind == SyntaxKind.EndrKeyword) depth--;
                    }
                }
                i++;
            }
        }
        else
            i++;
    }

    /// <summary>
    /// Recursively search the green subtree for a MacroParamToken with a digit suffix (\1..\9).
    /// Returns the token text if found, or null if none present.
    /// </summary>
    /// <summary>
    /// Check if a green subtree contains MacroParamTokens (\1..\9, \#) or _NARG references
    /// that need lazy resolution from the current macro frame.
    /// </summary>
    private static bool ContainsMacroParam(GreenNodeBase green)
    {
        if (green is GreenToken tok)
        {
            if (tok.Kind == SyntaxKind.MacroParamToken)
                return true;
            if (tok.Kind == SyntaxKind.IdentifierToken &&
                tok.Text.Equals("_NARG", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        for (int i = 0; i < green.ChildCount; i++)
        {
            var child = green.GetChild(i);
            if (child != null && ContainsMacroParam(child))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Resolve \1..\9, \#, _NARG in raw text against the current macro frame.
    /// Used for lazy resolution when SHIFT is active.
    /// Reports an error if \N references an arg that has been shifted past.
    /// </summary>
    private string ResolveMacroParamsInText(string text, MacroFrame frame)
    {
        for (int p = 9; p >= 1; p--)
        {
            var placeholder = $"\\{p}";
            if (!text.Contains(placeholder)) continue;
            int argIndex = p - 1 + frame.ShiftOffset;
            if (argIndex >= frame.Args.Count)
            {
                _diagnostics.Report(default, $"Macro argument \\{p} not defined (shifted past end)");
                text = text.Replace(placeholder, "");
            }
            else
            {
                text = text.Replace(placeholder, frame.Args[argIndex]);
            }
        }

        text = ResolveComputedArgs(text, frame);

        if (text.Contains("\\#"))
            text = text.Replace("\\#", frame.AllArgs());

        text = SubstituteOutsideStrings(text, NargPattern, frame.Narg.ToString());

        return text;
    }

    private static string? FindMacroParamToken(GreenNodeBase green)
    {
        if (green is GreenToken tok)
        {
            if (tok.Kind == SyntaxKind.MacroParamToken && tok.Text.Length == 2
                && tok.Text[1] >= '1' && tok.Text[1] <= '9')
                return tok.Text;
            return null;
        }
        for (int i = 0; i < green.ChildCount; i++)
        {
            var child = green.GetChild(i);
            if (child == null) continue;
            var found = FindMacroParamToken(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Returns true if the green subtree contains any CharLiteralToken.
    /// Used to exclude char-literal expressions from the pre-scan EQU phase, because
    /// their values depend on charmap state which isn't available during pre-scan.
    /// </summary>
    private static bool ContainsCharLiteral(GreenNodeBase green)
    {
        if (green is GreenToken tok)
            return tok.Kind == SyntaxKind.CharLiteralToken;
        for (int i = 0; i < green.ChildCount; i++)
        {
            var child = green.GetChild(i);
            if (child != null && ContainsCharLiteral(child)) return true;
        }
        return false;
    }

    private sealed record MacroDef(string Name, string Body);
}
