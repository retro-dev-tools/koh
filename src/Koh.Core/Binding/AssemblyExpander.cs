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
public sealed record ExpandedNode(SyntaxNode Node, string SourceFilePath = "");

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

    private const int MaxExpansionDepth = 64;

    public AssemblyExpander(DiagnosticBag diagnostics, SymbolTable symbols,
        ISourceFileResolver? fileResolver = null, CharMapManager? charMaps = null)
    {
        _diagnostics = diagnostics;
        _symbols = symbols;
        _fileResolver = fileResolver ?? new FileSystemResolver();
        _charMaps = charMaps ?? new CharMapManager(diagnostics);
    }

    /// <summary>The expander's charmap state, for sharing with the binder.</summary>
    internal CharMapManager CharMaps => _charMaps;

    /// <summary>Optional callback to retrieve the current PC for @ interpolation.</summary>
    internal Func<int>? GetCurrentPC { get; set; }

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
        while (i < siblings.Count)
        {
            var item = siblings[i];
            if (!item.IsNode) { i++; continue; }
            var node = item.AsNode!;

            // Check if this node's source text contains {EQUS} interpolation that needs
            // resolving before the parser can understand it. This handles cases like
            // DEF {prefix}banana EQU 1, PURGE {prefix}banana, etc.
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
                    output.Add(new ExpandedNode(node, _currentFilePath)); // INCBIN handled by binder Pass 2
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

            // RSRESET/RSSET — handle RS counter directives during expansion
            if (node.Kind == SyntaxKind.DirectiveStatement)
            {
                var kw = node.ChildTokens().FirstOrDefault();
                if (kw?.Kind is SyntaxKind.RsresetKeyword or SyntaxKind.RssetKeyword)
                {
                    HandleRsDirective(node);
                    i++;
                    continue; // consumed — don't emit to output
                }
            }

            output.Add(new ExpandedNode(node, _currentFilePath));
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
            var exprNodes = node.ChildNodes().ToList();
            if (exprNodes.Count > 0)
            {
                var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps, ResolveInterpolations);
                var value = evaluator.TryEvaluate(exprNodes[0].Green);
                if (value.HasValue)
                {
                    // = and REDEF are reassignable (SET semantics); EQU is immutable.
                    // DefineConstant handles the pre-scan idempotency case: if the pre-scan
                    // already defined this constant with the same value, it is a silent no-op.
                    // If a user writes FOO EQU x twice with different values, DefineConstant
                    // will report the duplicate-definition error.
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
    /// Evaluate a string expression for EQUS assignment. Handles string literals,
    /// REVCHAR(), and STRCAT().
    /// </summary>
    private string? EvaluateStringExpression(SyntaxNode exprNode)
    {
        // String literal
        var strToken = exprNode.ChildTokens()
            .FirstOrDefault(t => t.Kind == SyntaxKind.StringLiteral);
        if (strToken != null)
        {
            var raw = strToken.Text.Length >= 2 ? strToken.Text[1..^1] : strToken.Text;
            return ExpressionEvaluator.UnescapeString(raw);
        }

        // Function calls that return strings: REVCHAR, STRCAT, etc.
        if (exprNode.Kind == SyntaxKind.FunctionCallExpression)
        {
            var funcTokens = exprNode.ChildTokens().ToList();
            if (funcTokens.Count == 0) return null;

            switch (funcTokens[0].Kind)
            {
                case SyntaxKind.RevcharKeyword:
                {
                    // Collect numeric arguments
                    var argExprs = exprNode.ChildNodes().ToList();
                    var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps, ResolveInterpolations);
                    var bytes = new List<byte>();
                    foreach (var argExpr in argExprs)
                    {
                        var val = evaluator.TryEvaluate(argExpr.Green);
                        if (val.HasValue)
                            bytes.Add((byte)(val.Value & 0xFF));
                    }
                    if (bytes.Count > 0)
                        return _charMaps.ReverseCharMap(bytes.ToArray());
                    break;
                }

                case SyntaxKind.StruprKeyword:
                {
                    var argExprs = exprNode.ChildNodes().ToList();
                    if (argExprs.Count > 0)
                    {
                        var s = EvaluateStringExpression(argExprs[0]);
                        if (s != null) return s.ToUpperInvariant();
                    }
                    break;
                }

                case SyntaxKind.StrlwrKeyword:
                {
                    var argExprs = exprNode.ChildNodes().ToList();
                    if (argExprs.Count > 0)
                    {
                        var s = EvaluateStringExpression(argExprs[0]);
                        if (s != null) return s.ToLowerInvariant();
                    }
                    break;
                }

                case SyntaxKind.StrcharKeyword:
                {
                    var argExprs = exprNode.ChildNodes().ToList();
                    if (argExprs.Count >= 2)
                    {
                        var s = EvaluateStringExpression(argExprs[0]);
                        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps, ResolveInterpolations);
                        var idxVal = evaluator.TryEvaluate(argExprs[1].Green);
                        if (s != null && idxVal.HasValue)
                        {
                            int idx = (int)idxVal.Value;
                            if (idx >= 1 && idx <= s.Length)
                                return s[(idx - 1)..idx];
                        }
                    }
                    break;
                }
            }

            // Use ExpressionEvaluator's string evaluation for STRCAT, STRSUB, etc.
            var eval = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps);
            var result = eval.TryEvaluateString(exprNode.Green);
            if (result != null) return result;
        }

        return null;
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
    private void HandlePurge(SyntaxNode node)
    {
        var tokens = node.ChildTokens().ToList();
        for (int ti = 1; ti < tokens.Count; ti++)
        {
            if (tokens[ti].Kind == SyntaxKind.CommaToken) continue;
            if (tokens[ti].Kind == SyntaxKind.IdentifierToken)
            {
                var name = tokens[ti].Text;
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

        return args;
    }

    private void ExpandMacroCall(SyntaxNode node, List<ExpandedNode> output)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count == 0) return;

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
        int shiftOffset = 0;

        // _NARG save/restore uses try/finally so that it is guaranteed to be
        // restored even if expansion throws or hits the depth limit in a nested call.
        var prevNarg = _symbols.Lookup("_NARG");
        long? savedNarg = prevNarg?.State == SymbolState.Defined ? prevNarg.Value : null;
        _symbols.DefineOrRedefine("_NARG", args.Count - shiftOffset);

        try
        {
            var body = SubstituteMacroParams(macro.Body, args, shiftOffset);
            var expanded = SyntaxTree.Parse(body);
            var savedText = _currentSourceText;
            _currentSourceText = expanded.Text;
            try
            {
                var expandedChildren = expanded.Root.ChildNodesAndTokens().ToList();
                int j = 0;
                ExpandBodyList(expandedChildren, ref j, output);
            }
            finally
            {
                _currentSourceText = savedText;
            }
        }
        finally
        {
            // Restore _NARG to the caller's value. If it was undefined before this
            // macro call, leave it at the inner macro's count (RGBDS leaves _NARG
            // as the last macro's count when called from top-level context).
            if (savedNarg.HasValue)
                _symbols.DefineOrRedefine("_NARG", savedNarg.Value);

            _expansionDepth--;
        }
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
    private string SubstituteMacroParams(string body, IReadOnlyList<string> args, int shiftOffset)
    {
        _uniqueIdCounter++;
        int narg = args.Count - shiftOffset;

        // Substitute \9..\1 descending (avoid \1 matching prefix of \10 if extended)
        for (int p = 9; p >= 1; p--)
        {
            int argIndex = p - 1 + shiftOffset;
            var replacement = argIndex < args.Count ? args[argIndex] : "";
            body = body.Replace($"\\{p}", replacement);
        }

        // \@ → unique suffix per invocation
        body = body.Replace("\\@", $"_{_uniqueIdCounter}");

        // \# → all remaining args as comma-separated string
        if (body.Contains("\\#"))
        {
            var remaining = args.Skip(shiftOffset).ToList();
            body = body.Replace("\\#", string.Join(", ", remaining));
        }

        // _NARG → argument count as a literal number, baked into the body text.
        // This ensures that expressions like "db _NARG" evaluate correctly in Pass 2
        // regardless of any subsequent macro calls that modify the _NARG symbol.
        body = SubstituteOutsideStrings(body, NargPattern, narg.ToString());

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
        var body = CollectRepeatBody(siblings, ref i);

        if (bodyTextRaw.Contains("\\@"))
        {
            // Text-level expansion with \@ substitution per iteration.
            // Each iteration re-parses the body so \@ produces a unique suffix.
            for (int iter = 0; iter < count; iter++)
            {
                _uniqueIdCounter++;
                var iterText = bodyTextRaw.Replace("\\@", $"_{_uniqueIdCounter}");
                ExpandTextInline(iterText, output);
            }
        }
        else
        {
            // Node-level replay: the same immutable body list is re-walked each
            // iteration. This is safe because SyntaxNodeOrToken is immutable and
            // ExpandBodyList only reads (never mutates) its siblings list.
            // NOTE: the _conditional state machine is shared across iterations.
            // A body whose IF/ELIF/ELSE/ENDC blocks are balanced will always leave
            // _conditional in its pre-iteration state after each pass, which is
            // correct. A body with unbalanced conditionals is already malformed and
            // will have produced a diagnostic at the IF-collection stage.
            for (int iter = 0; iter < count; iter++)
            {
                int j = 0;
                ExpandBodyList(body, ref j, output);
            }
        }
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

        // Extract body text for variable + \@ substitution
        var bodyTextRaw = ExtractBodyText(forNode, PeekBodyNodes(siblings, i));
        var body = CollectRepeatBody(siblings, ref i);

        if (varName != null)
        {
            // The FOR variable must be substituted as text into each iteration's body
            // before re-parsing, because expression evaluation happens in Pass 2 (after
            // all expansion is complete). If we relied solely on the symbol table, all
            // iterations of "db I" would see I's last value, not per-iteration values.
            //
            // Text substitution uses word-boundary matching (\b) to avoid corrupting
            // longer identifiers (ITEM when variable is I). It also skips content inside
            // string literals to avoid corrupting quoted text.
            //
            // The symbol is ALSO defined via DefineOrRedefine so that DEF(var), IF var>N,
            // and any compile-time expression evaluation during expansion (e.g. EQU inside
            // the loop body, IF conditions) see the correct per-iteration value.
            var varPattern = new Regex($@"\b{Regex.Escape(varName)}\b");

            for (long v = start; step > 0 ? v < stop : v > stop; v += step)
            {
                _symbols.DefineOrRedefine(varName, v);

                _uniqueIdCounter++;
                var iterText = SubstituteOutsideStrings(bodyTextRaw, varPattern, v.ToString());
                iterText = iterText.Replace("\\@", $"_{_uniqueIdCounter}");
                ExpandTextInline(iterText, output);
            }
            // Variable retains its last value after the loop (RGBDS behavior).
        }
        else
        {
            // No variable — behave like REPT
            for (long v = start; step > 0 ? v < stop : v > stop; v += step)
            {
                int j = 0;
                ExpandBodyList(body, ref j, output);
            }
        }
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

                // Resolve the symbol
                string? resolved = ResolveInterpolationValue(name.Trim(), fmt?.Trim());
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

    private string? ResolveInterpolationValue(string name, string? fmt)
    {
        // Handle @ (current PC) — RGBDS defaults to uppercase hex with $ prefix
        if (name == "@" && GetCurrentPC != null)
            return FormatNumericValue(GetCurrentPC(), fmt ?? "#X");

        // Check EQUS constants first (string type)
        if (_equsConstants.TryGetValue(name, out var equsValue))
            return equsValue; // EQUS always returns raw string regardless of format

        // Check numeric symbols
        var sym = _symbols.Lookup(name);
        if (sym != null && sym.State == Symbols.SymbolState.Defined)
            return FormatNumericValue(sym.Value, fmt ?? "d");

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

    private sealed record MacroDef(string Name, string Body);
}
