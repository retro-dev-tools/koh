using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;
using Koh.Core.Text;

namespace Koh.Core.Binding;

/// <summary>
/// A single effective statement after all macro/REPT/IF expansion.
/// SourceFilePath tracks which file the node came from (for INCBIN path resolution).
/// FromMacroBody is true when the node was produced inside a macro body expansion —
/// used to allow local labels without a preceding global anchor (RGBDS behavior).
/// </summary>
public sealed record ExpandedNode(SyntaxNode Node, string SourceFilePath, bool WasInConditional, bool FromMacroBody, ExpansionTrace Trace);

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
    private readonly Dictionary<string, MacroDefinition> _macros = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _equsConstants = new(StringComparer.OrdinalIgnoreCase);
    private readonly CharMapManager _charMaps;
    private long _rsCounter; // RS counter for RB/RW/RSRESET/RSSET
    private readonly ISourceFileResolver _fileResolver;
    private readonly HashSet<string> _includeStack = new(StringComparer.OrdinalIgnoreCase);
    private int _uniqueIdCounter;
    private TextWriter _printOutput;
    private readonly InterpolationResolver _interpolation;
    private readonly SymbolResolutionContext _ownerContext;

    // Cache for parsed expression fragments (avoids repeated SyntaxTree.Parse for \<expr>)
    private readonly Dictionary<string, GreenNodeBase?> _expressionCache = new();
    private readonly TextReplayService _textReplay;

    private const int MaxStructuralDepth = 64;
    private const int MaxReplayDepth = 64;

    public AssemblyExpander(DiagnosticBag diagnostics, SymbolTable symbols,
        ISourceFileResolver? fileResolver = null, CharMapManager? charMaps = null,
        TextWriter? printOutput = null,
        SymbolResolutionContext ownerContext = default)
    {
        _diagnostics = diagnostics;
        _symbols = symbols;
        _fileResolver = fileResolver ?? new FileSystemResolver();
        _charMaps = charMaps ?? new CharMapManager(diagnostics);
        _printOutput = printOutput ?? Console.Error;
        _ownerContext = ownerContext.OwnerId != null
            ? ownerContext
            : new SymbolResolutionContext("<anonymous>");
        _interpolation = new InterpolationResolver(symbols, _equsConstants, diagnostics);
        _textReplay = new TextReplayService(_diagnostics, _interpolation);
    }

    /// <summary>The expander's charmap state, for sharing with the binder.</summary>
    internal CharMapManager CharMaps => _charMaps;

    /// <summary>Optional callback to retrieve the current PC for @ interpolation.</summary>
    internal Func<int>? GetCurrentPC
    {
        get => _interpolation.GetCurrentPC;
        set => _interpolation.GetCurrentPC = value;
    }

    /// <summary>Look up an EQUS constant by name. Returns null if not found.</summary>
    internal string? LookupEqus(string name) =>
        _equsConstants.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Optional callback to resolve SECTION(@) and SECTION(label) in string interpolation.
    /// </summary>
    internal Func<string, string?>? SectionNameResolver
    {
        get => _interpolation.SectionNameResolver;
        set => _interpolation.SectionNameResolver = value;
    }

    public List<ExpandedNode> Expand(SyntaxTree tree)
    {
        var output = new List<ExpandedNode>();
        var ctx = new ExpansionContext
        {
            SourceText = tree.Text,
            FilePath = tree.Text.FilePath
        };
        _diagnostics.CurrentFilePath = ctx.FilePath;
        // Seed include stack with root file for circular detection
        if (!string.IsNullOrEmpty(ctx.FilePath))
            _includeStack.Add(ctx.FilePath);
        var children = tree.Root.ChildNodesAndTokens().ToList();

        // Pre-scan: define all EQU/SET/DEF constants in the root node list before the main
        // expansion walk. This makes forward-declared constants visible to DS count expressions
        // in Pass 1, preventing a size divergence between Pass 1 and Pass 2.
        // Constants whose values depend on other forward-declared constants are resolved in
        // up to two passes of the pre-scan; circular definitions are silently skipped (they
        // will produce an "undefined symbol" diagnostic during evaluation).
        PreScanEquConstants(children);

        int i = 0;
        var topLc = ExpandBodyList(children, ref i, output, ctx);
        if (topLc == LoopControl.Break)
            _diagnostics.Report(default, "BREAK escaped loop scope");

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

            if (pass == maxPasses - 1)
            {
                _diagnostics.Report(default,
                    $"EQU pre-scan did not converge after {maxPasses} passes; some forward references may be unresolved",
                    Diagnostics.DiagnosticSeverity.Warning);
            }
        }
    }

    /// <summary>
    /// Like EarlyDefineEqu but silently skips when the value expression cannot be resolved
    /// (instead of producing a diagnostic). Used during pre-scan where unresolved forward
    /// references are expected — they are retried on the second pass or during the normal walk.
    /// </summary>
    private void EarlyDefineEquNoError(SyntaxNode node)
    {
        // Perf: extract first 3 tokens by iterating rather than materializing a List<SyntaxToken>.
        // We only ever need tok0, tok[nameIdx], and tok[nameIdx+1] — at most 3 slots.
        SyntaxToken? tok0 = null, tok1 = null, tok2 = null;
        int tokenCount = 0;
        foreach (var t in node.ChildTokens())
        {
            if (tokenCount == 0) tok0 = t;
            else if (tokenCount == 1) tok1 = t;
            else if (tokenCount == 2) tok2 = t;
            tokenCount++;
            if (tokenCount > 2) break; // we have all we need
        }
        if (tokenCount < 2) return;

        // Determine nameIdx: if first token is DEF/REDEF prefix, name is at index 1
        int nameIdx = tok0!.Kind is SyntaxKind.DefKeyword or SyntaxKind.RedefKeyword ? 1 : 0;

        SyntaxToken nameTok = nameIdx == 0 ? tok0! : tok1!;
        SyntaxToken? kwTok  = nameIdx == 0 ? tok1  : tok2;
        if (tokenCount < nameIdx + 2 || kwTok == null) return;
        if (nameTok.Kind != SyntaxKind.IdentifierToken) return;

        var kwKind = kwTok.Kind;
        if (kwKind is SyntaxKind.EquKeyword or SyntaxKind.EqualsToken)
        {
            // Perf: FirstOrDefault avoids materializing the full ChildNodes() list
            var firstExpr = node.ChildNodes().FirstOrDefault();
            if (firstExpr == null) return;
            // Skip char literals in the pre-scan: their values depend on charmap state which is
            // not fully known until the sequential expansion walk processes all CHARMAP directives.
            // Allowing the pre-scan to define them with the wrong (ASCII) value would conflict
            // with the later correct (charmap-mapped) definition from EarlyDefineEqu.
            if (ContainsCharLiteral(firstExpr.Green)) return;
            // Use a silent evaluator — don't report missing-symbol errors during pre-scan
            var evaluator = new ExpressionEvaluator(_symbols, DiagnosticBag.Null, () => 0, 0, _charMaps);
            var value = evaluator.TryEvaluate(firstExpr.Green);
            if (!value.HasValue) return; // unresolvable at this point — retry on next pass
            if (kwKind == SyntaxKind.EqualsToken || tok0.Kind == SyntaxKind.RedefKeyword)
                _symbols.DefineOrRedefine(nameTok.Text, value.Value, _ownerContext);
            else
                // EQU: only define if not already defined (avoid duplicate-definition diagnostic)
                _symbols.DefineConstantIfAbsent(nameTok.Text, value.Value, node, _ownerContext);
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

    private LoopControl ExpandBodyList(IReadOnlyList<SyntaxNodeOrToken> siblings,
        ref int i, List<ExpandedNode> output, ExpansionContext ctx)
    {
        while (i < siblings.Count)
        {
            var item = siblings[i];
            if (!item.IsNode) { i++; continue; }
            var node = item.AsNode!;

            // Lazy macro param resolution: if inside a macro frame with SHIFT,
            // MacroParamTokens (\1..\9, \#) survive in the tree and need resolving.
            // Do NOT apply this to block-structured nodes (REPT, FOR, conditional, macro defs)
            // because those require sibling context for body collection and must go through
            // their dedicated expansion paths (ExpandRept, ExpandFor, HandleConditional, etc.).
            // _NARG inside REPT _NARG is resolved via the symbol table lookup in ExpandRept's
            // expression evaluator — not through text substitution here.
            bool isBlockNode = node.Kind is SyntaxKind.RepeatDirective
                or SyntaxKind.ConditionalDirective
                or SyntaxKind.MacroDefinition;
            if (!isBlockNode && !_conditional.IsSuppressed && ctx.CurrentMacroFrame != null && ContainsMacroParam(node.Green))
            {
                var frame = ctx.CurrentMacroFrame;
                var rawText = ctx.SourceText != null
                    ? ctx.SourceText.ToString(node.FullSpan)
                    : (node.Green.ToString() ?? "");
                var resolved = ResolveMacroParamsInText(rawText, frame);
                if (resolved != rawText)
                {
                    var lc = ExpandTextInline(resolved, output, ctx, node.FullSpan, TextReplayReason.MacroParameterConcatenation);
                    i++;
                    if (lc == LoopControl.Break) return LoopControl.Break;
                    continue;
                }
            }

            // {EQUS} interpolation — DEF {prefix}banana EQU 1, PURGE {prefix}banana, etc.
            // Skip block-structured nodes for the same reason as the macro param path above:
            // they need sibling context and have dedicated expansion handlers.
            if (!isBlockNode && !_conditional.IsSuppressed && ctx.SourceText != null && NeedsInterpolation(node))
            {
                var rawText = ctx.SourceText.ToString(node.FullSpan);
                var resolved = ResolveInterpolations(rawText);
                if (resolved != rawText)
                {
                    var lc = ExpandTextInline(resolved, output, ctx, node.FullSpan, TextReplayReason.EqusReplay);
                    i++;
                    if (lc == LoopControl.Break) return LoopControl.Break;
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
                HandleConditional(node, ctx);
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
                    // Perf: count tokens directly to avoid ToList() allocation
                    if (HasChildTokensBeyond(node, 1))
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
                        CollectMacroBody(siblings, ref i, macroName, ctx);
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
                    // Perf: count tokens directly to avoid ToList() allocation
                    if (HasChildTokensBeyond(node, 1))
                        _diagnostics.Report(node.FullSpan, "Unexpected tokens after ENDR");
                }
                if (kw?.Kind == SyntaxKind.ReptKeyword)
                    ExpandRept(node, siblings, ref i, output, ctx);
                else if (kw?.Kind == SyntaxKind.ForKeyword)
                    ExpandFor(node, siblings, ref i, output, ctx);
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
                    var lc = ExpandTextInline(equsValue, output, ctx, node.FullSpan, TextReplayReason.EqusReplay);
                    i++;
                    if (lc == LoopControl.Break) return LoopControl.Break;
                    continue;
                }
                ExpandMacroCall(node, output, ctx);
                i++;
                continue;
            }

            // INCLUDE — textual inclusion of another source file
            if (node.Kind == SyntaxKind.IncludeDirective)
            {
                var kw = node.ChildTokens().FirstOrDefault();
                if (kw?.Kind == SyntaxKind.IncludeKeyword)
                    ExpandInclude(node, output, ctx);
                else if (kw?.Kind == SyntaxKind.IncbinKeyword)
                    output.Add(new ExpandedNode(node, ctx.FilePath, _conditional.Depth > 0, ctx.MacroBodyDepth > 0, ctx.Trace)); // INCBIN handled by binder Pass 2
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
                // Perf: FirstOrDefault avoids ToList() allocation for the keyword peek
                var firstSymTok = node.ChildTokens().FirstOrDefault();
                if (firstSymTok?.Kind == SyntaxKind.PurgeKeyword)
                {
                    HandlePurge(node, ctx);
                    i++;
                    continue; // consumed — don't emit to output
                }
                EarlyDefineEqu(node, ctx);
                EarlyProcessCharmap(node, ctx);
            }

            // Perf: single DirectiveStatement check with one FirstOrDefault() call covers
            // SHIFT, RSRESET/RSSET, BREAK, and PRINT/PRINTLN — avoids two separate kind checks
            // and two separate FirstOrDefault() enumerations over the same node.
            if (node.Kind == SyntaxKind.DirectiveStatement)
            {
                var kw = node.ChildTokens().FirstOrDefault();

                // SHIFT — advance the macro argument window
                if (kw?.Kind == SyntaxKind.ShiftKeyword)
                {
                    if (ctx.CurrentMacroFrame == null)
                    {
                        _diagnostics.Report(node.FullSpan, "SHIFT used outside of a macro");
                        i++;
                        continue;
                    }
                    var frame = ctx.CurrentMacroFrame;
                    if (frame.ShiftOffset >= frame.Args.Count)
                        _diagnostics.Report(node.FullSpan,
                            "Cannot shift macro arguments past their end",
                            DiagnosticSeverity.Warning);
                    frame.ShiftOffset++;
                    _symbols.DefineOrRedefine("_NARG", frame.Narg, _ownerContext);
                    i++;
                    continue;
                }

                // RSRESET/RSSET — handle RS counter directives during expansion
                if (kw?.Kind is SyntaxKind.RsresetKeyword or SyntaxKind.RssetKeyword)
                {
                    HandleRsDirective(node, ctx);
                    i++;
                    continue; // consumed — don't emit to output
                }

                // BREAK — signal loop exit
                if (kw?.Kind == SyntaxKind.BreakKeyword)
                {
                    if (ctx.LoopDepth == 0)
                    {
                        _diagnostics.Report(node.FullSpan, "BREAK used outside of a REPT/FOR loop");
                        i++;
                        continue;
                    }
                    i++;
                    return LoopControl.Break;
                }

                // PRINT/PRINTLN inside loops — resolve at expansion time so per-iteration output is correct
                if (ctx.LoopDepth > 0 && kw?.Kind is SyntaxKind.PrintKeyword or SyntaxKind.PrintlnKeyword)
                {
                    // The StringLiteral may be nested inside a LiteralExpression child node,
                    // so use a recursive descent to find it rather than ChildTokens() which
                    // only returns direct token children.
                    var msgToken = FindTokenInSubtree(node.Green, SyntaxKind.StringLiteral);
                    if (msgToken != null)
                    {
                        var text = ExpressionEvaluator.InterpretStringLiteral(msgToken);
                        text = ResolveInterpolations(text);
                        _printOutput.Write(text);
                    }
                    if (kw!.Kind == SyntaxKind.PrintlnKeyword)
                        _printOutput.WriteLine();
                    i++;
                    continue; // consumed — don't emit to output (already printed)
                }
            }

            // Check for macro parameter tokens (\1..\9) outside macro context.
            // Must walk the full green subtree because \1 may be inside a nested expression node
            // (e.g. "println \1" → DirectiveStatement[PrintlnKw, LiteralExpression[MacroParamToken]]).
            if (ctx.StructuralDepth == 0 && ctx.ReplayDepth == 0)
            {
                var macroParam = FindMacroParamToken(node.Green);
                if (macroParam != null)
                    _diagnostics.Report(node.FullSpan, $"Macro argument {macroParam} used outside of a macro");
            }

            output.Add(new ExpandedNode(node, ctx.FilePath, _conditional.Depth > 0, ctx.MacroBodyDepth > 0, ctx.Trace));
            i++;
        }
        return LoopControl.Continue;
    }

    private void EarlyDefineEqu(SyntaxNode node, ExpansionContext ctx)
    {
        // Perf: extract first 3 tokens by iterating rather than materializing a List<SyntaxToken>.
        // We only ever need tok0, tok[nameIdx], and tok[nameIdx+1] — at most 3 slots.
        SyntaxToken? tok0 = null, tok1 = null, tok2 = null;
        int tokenCount = 0;
        foreach (var t in node.ChildTokens())
        {
            if (tokenCount == 0) tok0 = t;
            else if (tokenCount == 1) tok1 = t;
            else if (tokenCount == 2) tok2 = t;
            tokenCount++;
            if (tokenCount > 2) break; // we have all we need
        }
        if (tokenCount < 2) return;

        // Handle DEF/REDEF prefix: if first token is a prefix keyword, name is at index 1
        int nameIdx = tok0!.Kind is SyntaxKind.DefKeyword or SyntaxKind.RedefKeyword ? 1 : 0;

        SyntaxToken nameTok = nameIdx == 0 ? tok0! : tok1!;
        SyntaxToken? kwTok  = nameIdx == 0 ? tok1  : tok2;
        if (tokenCount < nameIdx + 2 || kwTok == null) return;

        if (nameTok.Kind == SyntaxKind.IdentifierToken &&
            kwTok.Kind is SyntaxKind.EquKeyword or SyntaxKind.EqualsToken)
        {
            // Check for built-in symbol protection
            if (tok0.Kind == SyntaxKind.RedefKeyword && BuiltinSymbols.Contains(nameTok.Text))
            {
                _diagnostics.Report(node.FullSpan, $"Cannot redefine built-in symbol '{nameTok.Text}'");
                return;
            }

            // Perf: FirstOrDefault avoids materializing the full ChildNodes() list
            var firstExprEqu = node.ChildNodes().FirstOrDefault();
            if (firstExprEqu != null)
            {
                var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps, ResolveInterpolations);
                var value = evaluator.TryEvaluate(firstExprEqu.Green);
                if (value.HasValue)
                {
                    // = and REDEF are reassignable (SET semantics); EQU is immutable.
                    if (kwTok.Kind == SyntaxKind.EqualsToken || tok0.Kind == SyntaxKind.RedefKeyword)
                        _symbols.DefineOrRedefine(nameTok.Text, value.Value, _ownerContext);
                    else
                        _symbols.DefineConstant(nameTok.Text, value.Value, node, _ownerContext);
                }
            }
        }
        else if (nameTok.Kind == SyntaxKind.IdentifierToken &&
                 kwTok.Kind is SyntaxKind.RbKeyword or SyntaxKind.RwKeyword or SyntaxKind.RlKeyword)
        {
            int multiplier = kwTok.Kind switch
            {
                SyntaxKind.RwKeyword => 2,
                SyntaxKind.RlKeyword => 4,
                _ => 1,
            };
            long count = 1; // default count is 1

            // Perf: FirstOrDefault avoids materializing the full ChildNodes() list
            var firstExprRs = node.ChildNodes().FirstOrDefault();
            if (firstExprRs != null)
            {
                var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps);
                var value = evaluator.TryEvaluate(firstExprRs.Green);
                if (value.HasValue) count = value.Value;
            }

            _symbols.DefineConstant(nameTok.Text, _rsCounter, node, _ownerContext);
            _rsCounter += count * multiplier;
        }
        else if (nameTok.Kind == SyntaxKind.IdentifierToken && kwTok.Kind == SyntaxKind.EqusKeyword)
        {
            // name EQUS expr — evaluate string expression
            // Perf: FirstOrDefault avoids materializing the full ChildNodes() list
            var firstExprEqus = node.ChildNodes().FirstOrDefault();
            if (firstExprEqus != null)
            {
                var value = EvaluateStringExpression(firstExprEqus, ctx);
                if (value != null)
                {
                    _equsConstants[nameTok.Text] = value;
                    _symbols.DefineStringConstant(nameTok.Text, value, node, _ownerContext);
                }
            }
        }
    }

    /// <summary>
    /// Evaluate a string expression for EQUS assignment. Handles string literals, REVCHAR(),
    /// and all string-returning functions (STRUPR, STRLWR, STRCAT, READFILE, etc.).
    /// </summary>
    private string? EvaluateStringExpression(SyntaxNode exprNode, ExpansionContext ctx)
    {
        // Use the full ExpressionEvaluator with string support
        var evaluator = CreateStringEvaluator(ctx);
        var result = evaluator.TryEvaluateString(exprNode.Green);
        if (result != null) return result;

        // ++ concatenation (BinaryExpression)
        if (exprNode.Kind == SyntaxKind.BinaryExpression)
        {
            // Perf: scan tokens directly without ToList() to detect ++
            bool hasPlusPlus = false;
            foreach (var t in exprNode.ChildTokens())
            {
                if (t.Kind == SyntaxKind.PlusPlusToken) { hasPlusPlus = true; break; }
            }
            if (hasPlusPlus)
            {
                // Perf: extract first and last child nodes by iterating — avoids List<> allocation.
                // ++ concatenation is binary so we need exactly children[0] and children[^1].
                SyntaxNode? firstChild = null, lastChild = null;
                int childCount = 0;
                foreach (var c in exprNode.ChildNodes())
                {
                    if (childCount == 0) firstChild = c;
                    lastChild = c;
                    childCount++;
                }
                if (childCount >= 2)
                {
                    var left  = EvaluateStringExpression(firstChild!, ctx);
                    var right = EvaluateStringExpression(lastChild!,  ctx);
                    if (left != null && right != null)
                        return left + right;
                }
            }
        }

        // Function calls
        if (exprNode.Kind == SyntaxKind.FunctionCallExpression)
        {
            // Perf: FirstOrDefault avoids ToList() allocation for the keyword peek
            var firstFuncTok = exprNode.ChildTokens().FirstOrDefault();
            if (firstFuncTok == null) return null;
            var funcKind = firstFuncTok.Kind;
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
                        var s = EvaluateStringExpression(argExprs[0], ctx);
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
                        var s = EvaluateStringExpression(argExprs[0], ctx);
                        return s?.ToUpperInvariant();
                    }
                    break;
                }
                case SyntaxKind.StrlwrKeyword:
                {
                    if (argExprs.Count > 0)
                    {
                        var s = EvaluateStringExpression(argExprs[0], ctx);
                        return s?.ToLowerInvariant();
                    }
                    break;
                }
                case SyntaxKind.StrcatKeyword:
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var argExpr in argExprs)
                    {
                        var s = EvaluateStringExpression(argExpr, ctx);
                        if (s == null) return null;
                        sb.Append(s);
                    }
                    return sb.ToString();
                }
                case SyntaxKind.StrsubKeyword:
                {
                    if (argExprs.Count >= 2)
                    {
                        var s = EvaluateStringExpression(argExprs[0], ctx);
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
                        var filename = EvaluateStringExpression(argExprs[0], ctx);
                        if (filename == null) return null;
                        var resolved = _fileResolver.ResolvePath(ctx.FilePath, filename);
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
    private ExpressionEvaluator CreateStringEvaluator(ExpansionContext ctx)
    {
        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0)
        {
            EqusResolver = LookupEqus,
            CharlenResolver = s => _charMaps.CharLen(s),
            IncharmapResolver = s => _charMaps.InCharMap(s),
            ReadfileResolver = (path, limit) => ResolveReadfile(path, limit, ctx.FilePath),
        };
        return evaluator;
    }

    /// <summary>
    /// Shared file reading logic for READFILE function.
    /// </summary>
    internal string? ResolveReadfile(string path, int? limit, string basePath)
    {
        var resolved = _fileResolver.ResolvePath(basePath, path);
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

    private void EarlyProcessCharmap(SyntaxNode node, ExpansionContext ctx)
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
                                    _diagnostics.Report(node.FullSpan,
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

    private void HandlePurge(SyntaxNode node, ExpansionContext ctx)
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
                var sym = _symbols.Lookup(name, _ownerContext);
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

    private void HandleRsDirective(SyntaxNode node, ExpansionContext ctx)
    {
        // Perf: FirstOrDefault avoids ToList() allocation for keyword peek
        var firstTok = node.ChildTokens().FirstOrDefault();
        if (firstTok == null) return;

        switch (firstTok.Kind)
        {
            case SyntaxKind.RsresetKeyword:
                _rsCounter = 0;
                break;
            case SyntaxKind.RssetKeyword:
            {
                // Perf: FirstOrDefault avoids ToList() allocation
                var firstExpr = node.ChildNodes().FirstOrDefault();
                if (firstExpr != null)
                {
                    var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps);
                    var value = evaluator.TryEvaluate(firstExpr.Green);
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

    private void HandleConditional(SyntaxNode node, ExpansionContext ctx)
    {
        var keyword = ((GreenToken)((GreenNode)node.Green).GetChild(0)!).Kind;

        // Check for trailing tokens after ENDC or ELSE
        // Perf: count tokens directly to avoid ToList() allocation
        if (keyword is SyntaxKind.EndcKeyword or SyntaxKind.ElseKeyword)
        {
            if (HasChildTokensBeyond(node, 1))
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
        ref int i, string macroName, ExpansionContext ctx)
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
            _diagnostics.Report(macroNode.FullSpan, $"Macro '{macroName}' has no matching ENDM");
            return;
        }

        if (ctx.SourceText != null)
        {
            var bodyText = ctx.SourceText.ToString(new TextSpan(bodyStart, bodyEnd - bodyStart)).Trim();
            _macros[macroName] = new MacroDefinition(macroName, bodyText, macroNode.FullSpan, ctx.FilePath);
            // Register macro in symbol table using root owner context (not included file's path)
            _symbols.DefineMacro(macroName, macroNode, _ownerContext);
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

    private void ExpandMacroCall(SyntaxNode node, List<ExpandedNode> output, ExpansionContext ctx)
    {
        var tokens = node.ChildTokens().ToList();
        if (tokens.Count == 0) return;

        // If the call text contains \# (MacroParamToken), resolve it against
        // the parent macro frame before looking up the macro name and args.
        // Perf: manual loop avoids LINQ delegate allocation on every macro call
        bool hasBackslashHash = false;
        foreach (var t in tokens)
        {
            if (t.Kind == SyntaxKind.MacroParamToken && t.Text == "\\#") { hasBackslashHash = true; break; }
        }
        if (hasBackslashHash && ctx.CurrentMacroFrame != null)
        {
            var parentFrame = ctx.CurrentMacroFrame;
            string rawText;
            if (ctx.SourceText != null)
            {
                rawText = ctx.SourceText.ToString(node.FullSpan);
            }
            else
            {
                // Cold path: no source text — build from token texts
                var sb = new System.Text.StringBuilder();
                foreach (var t in tokens) sb.Append(t.Text);
                rawText = sb.ToString();
            }
            var resolved = rawText.Replace("\\#", parentFrame.AllArgs());
            ExpandTextInline(resolved, output, ctx, node.FullSpan, TextReplayReason.MacroParameterConcatenation);
            return;
        }

        var name = tokens[0].Text;
        if (!_macros.TryGetValue(name, out var macro))
        {
            _diagnostics.Report(node.FullSpan, $"Unexpected identifier '{name}'");
            return;
        }

        // Structural depth check
        var args = CollectMacroArgs(tokens, startIndex: 1);
        _uniqueIdCounter++;
        var frame = new MacroFrame(args) { UniqueId = _uniqueIdCounter };
        var macroCtx = ctx.ForMacro(frame, macro);

        if (macroCtx.StructuralDepth > MaxStructuralDepth)
        {
            _diagnostics.Report(node.FullSpan,
                $"Maximum structural depth ({MaxStructuralDepth}) exceeded");
            return;
        }

        var prevNarg = _symbols.Lookup("_NARG", _ownerContext);
        long? savedNarg = prevNarg?.State == SymbolState.Defined ? prevNarg.Value : null;
        _symbols.DefineOrRedefine("_NARG", frame.Narg, _ownerContext);

        try
        {
            ExpandMacroBody(macro, frame, output, macroCtx);
        }
        finally
        {
            if (savedNarg.HasValue)
                _symbols.DefineOrRedefine("_NARG", savedNarg.Value, _ownerContext);
        }
    }

    /// <summary>
    /// Expand a macro body. Two paths:
    /// 1. Fast path: macros without param references (\1..\9, \@, \#, _NARG, \&lt;expr&gt;)
    ///    replay the pre-parsed tree directly — no text substitution, no reparse.
    /// 2. Text path: macros with param references go through text substitution + reparse
    ///    (necessary because RGBDS params can concatenate to form new tokens).
    /// </summary>
    private void ExpandMacroBody(MacroDefinition macro, MacroFrame frame, List<ExpandedNode> output, ExpansionContext ctx)
    {
        if (macro.RequiresTextSubstitution)
        {
            var body = _textReplay.SubstituteMacroParams(macro.RawBody, frame, macro.ContainsShift,
                _symbols, _expressionCache);
            ExpandTextInline(body, output, ctx, macro.DefinitionSpan, TextReplayReason.MacroParameterConcatenation);
        }
        else
        {
            // Fast path — replay pre-parsed tree without reparsing.
            // Interpolation ({symbol}) is handled per-node by ExpandBodyList's
            // NeedsInterpolation check; no upfront text resolution needed.
            ExpandParsedTree(macro.ParsedBody, output, ctx);
        }
    }

    /// <summary>
    /// Expand a pre-parsed syntax tree through the expansion kernel without reparsing.
    /// Used for macro bodies that don't require text-level parameter substitution.
    /// Structural depth is already checked at the call site (ExpandMacroCall).
    /// ReplayDepth is not incremented — this is structural execution, not replay.
    /// </summary>
    private void ExpandParsedTree(SyntaxTree tree, List<ExpandedNode> output, ExpansionContext ctx)
    {
        var treeCtx = ctx with { SourceText = tree.Text };
        var children = tree.Root.ChildNodesAndTokens().ToList();
        int j = 0;
        ExpandBodyList(children, ref j, output, treeCtx);
    }

    // =========================================================================
    // INCLUDE — textual file inclusion
    // =========================================================================

    private void ExpandInclude(SyntaxNode node, List<ExpandedNode> output, ExpansionContext ctx)
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

        var resolved = _fileResolver.ResolvePath(ctx.FilePath, filePath);

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

        // Structural depth check (ForInclude increments by 1)
        if (ctx.StructuralDepth + 1 > MaxStructuralDepth)
        {
            _diagnostics.Report(node.FullSpan,
                $"Maximum structural depth ({MaxStructuralDepth}) exceeded");
            return;
        }

        _includeStack.Add(resolved);

        try
        {
            var source = _fileResolver.ReadAllText(resolved);

            // Substitute \@ in the included file's text using the current invocation's unique ID.
            // This ensures that \@ labels in an included file share the same unique suffix as the
            // macro call (or REPT iteration) that INCLUDEd them, matching RGBDS.
            // Note: substitution is routed through TextReplayService for consistency, but the
            // subsequent SyntaxTree.Parse is direct new-source intake, not replay — it does not
            // go through ParseForReplay because INCLUDE is not replay-driven re-expansion.
            if (ctx.CurrentMacroFrame != null)
            {
                source = TextReplayService.SubstituteUniqueId(source, ctx.CurrentMacroFrame.UniqueId);
            }
            else if (ctx.LoopUniqueId != 0)
            {
                source = TextReplayService.SubstituteUniqueId(source, ctx.LoopUniqueId);
            }

            var includeText = Text.SourceText.From(source, resolved);
            var includeTree = Syntax.SyntaxTree.Parse(includeText);

            var finalIncludeCtx = ctx.ForInclude(resolved, includeText, node.FullSpan);
            _diagnostics.CurrentFilePath = resolved;

            try
            {
                var children = includeTree.Root.ChildNodesAndTokens().ToList();
                int j = 0;
                ExpandBodyList(children, ref j, output, finalIncludeCtx);
            }
            finally
            {
                _diagnostics.CurrentFilePath = ctx.FilePath;
            }
        }
        catch (IOException ex)
        {
            _diagnostics.Report(node.FullSpan, $"Cannot read included file '{filePath}': {ex.Message}");
        }
        finally
        {
            _includeStack.Remove(resolved);
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



    // =========================================================================
    // REPT/FOR expansion
    // =========================================================================

    private List<SyntaxNodeOrToken> CollectRepeatBody(
        IReadOnlyList<SyntaxNodeOrToken> siblings, ref int i, TextSpan headerSpan)
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

        _diagnostics.Report(headerSpan, "REPT/FOR without matching ENDR");
        return body;
    }

    private void ExpandRept(SyntaxNode reptNode,
        IReadOnlyList<SyntaxNodeOrToken> siblings, ref int i,
        List<ExpandedNode> output, ExpansionContext ctx)
    {
        // Perf: FirstOrDefault avoids materializing the full ChildNodes() list
        var reptCountExpr = reptNode.ChildNodes().FirstOrDefault();
        int count = 0;
        if (reptCountExpr != null)
        {
            var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps);
            var val = evaluator.TryEvaluate(reptCountExpr.Green);
            if (val.HasValue) count = (int)val.Value;
        }
        if (count < 0) count = 0;

        // Peek body nodes to extract text, then collect to advance cursor past ENDR
        var peekBody = PeekBodyNodes(siblings, i);
        var bodyTextRaw = TextReplayService.ExtractBodyText(reptNode, peekBody, ctx);
        var bodyNodes = CollectRepeatBody(siblings, ref i, reptNode.FullSpan);

        // Classify: structural replay if no \@, text replay if \@ present
        var plan = TextReplayService.ClassifyReptBody(bodyTextRaw);

        var condDepthBefore = _conditional.Depth;
        if (plan.Kind == BodyReplayKind.Structural)
        {
            var lc = ExpandReptStructural(bodyNodes, count, reptNode, output, ctx);
            if (_conditional.Depth != condDepthBefore)
                _diagnostics.Report(reptNode.FullSpan, "Unbalanced IF/ENDC inside REPT body");
            _ = lc; // loop control already handled inside ExpandReptStructural
        }
        else
        {
            for (int iter = 0; iter < count; iter++)
            {
                _uniqueIdCounter++;
                var iterUniqueId = _uniqueIdCounter;
                var iterFrame = ExpansionFrame.ForRept(ctx.FilePath, reptNode.FullSpan, iter);
                var iterCtx = ctx.ForLoop(iterFrame, iterUniqueId);

                // Substitute \@ only at the REPT level; macro calls inside will
                // get their own \@ via SubstituteMacroParams.
                var iterText = TextReplayService.SubstituteUniqueId(bodyTextRaw, iterUniqueId);
                var lc = ExpandTextInline(iterText, output, iterCtx, reptNode.FullSpan, TextReplayReason.UniqueLabelSubstitution);
                if (lc == LoopControl.Break)
                {
                    // BREAK may have fired inside an IF block; reset conditional state to the
                    // depth before this iteration so the balance check doesn't fire a false positive.
                    _conditional.ResetToDepth(condDepthBefore);
                    break;
                }
            }
            if (_conditional.Depth != condDepthBefore)
                _diagnostics.Report(reptNode.FullSpan, "Unbalanced IF/ENDC inside REPT body");
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
        List<ExpandedNode> output, ExpansionContext ctx)
    {
        // Perf: extract up to 4 child nodes by iterating rather than materializing a List<SyntaxNode>.
        // FOR header has exactly 4 slots: varName, start, stop, step.
        SyntaxNode? exprNode0 = null, exprNode1 = null, exprNode2 = null, exprNode3 = null;
        int exprCount = 0;
        foreach (var n in forNode.ChildNodes())
        {
            switch (exprCount)
            {
                case 0: exprNode0 = n; break;
                case 1: exprNode1 = n; break;
                case 2: exprNode2 = n; break;
                case 3: exprNode3 = n; break;
            }
            exprCount++;
            if (exprCount > 3) break;
        }

        string? varName = null;
        if (exprNode0 != null && exprNode0.Kind == SyntaxKind.NameExpression)
            varName = exprNode0.ChildTokens().FirstOrDefault()?.Text;

        var evaluator = new ExpressionEvaluator(_symbols, _diagnostics, () => 0, 0, _charMaps);
        long start = exprNode1 != null ? evaluator.TryEvaluate(exprNode1.Green) ?? 0 : 0;
        long stop  = exprNode2 != null ? evaluator.TryEvaluate(exprNode2.Green) ?? 0 : 0;
        long step  = exprNode3 != null ? evaluator.TryEvaluate(exprNode3.Green) ?? 1 : 1;

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

        // Extract body text for classification and potential substitution
        var peekForBody = PeekBodyNodes(siblings, i);
        var bodyTextRaw = TextReplayService.ExtractBodyText(forNode, peekForBody, ctx);
        var forBodyNodes = CollectRepeatBody(siblings, ref i, forNode.FullSpan);

        // Classify: can we replay structurally (symbol table lookup per iteration)?
        // Structural replay emits synthetic REDEF nodes before each body so the Binder's
        // Pass 2 can re-evaluate the per-iteration variable value without re-parsing.
        // Text replay bakes the value into the re-parsed node text — used when the variable
        // appears in token-shaping positions or when \@ substitution is required.
        int iterIndex = 0;
        if (varName != null)
        {
            var plan = TextReplayService.ClassifyForBody(bodyTextRaw, varName);

            if (plan.Kind == BodyReplayKind.Structural)
            {
                // Structural path: all variable references are IdentifierTokens.
                // Emit synthetic REDEF nodes so Pass 2 evaluates the correct per-iteration value.
                ExpandForStructural(forBodyNodes, varName, start, stop, step,
                    forNode, output, ctx);
            }
            else
            {
                // Text replay path: use positions from classification (avoids reparsing)
                var positions = plan.IdentifierPositions ?? [];
                var condDepthBefore = _conditional.Depth;

                for (long v = start; step > 0 ? v < stop : v > stop; v += step, iterIndex++)
                {
                    _symbols.DefineOrRedefine(varName, v, _ownerContext);

                    _uniqueIdCounter++;
                    var iterUniqueId = _uniqueIdCounter;
                    var iterFrame = ExpansionFrame.ForFor(ctx.FilePath, forNode.FullSpan, varName, iterIndex);
                    var iterCtx = ctx.ForLoop(iterFrame, iterUniqueId);

                    var iterText = TextReplayService.SubstituteAtPositions(bodyTextRaw, positions, v.ToString());
                    iterText = TextReplayService.SubstituteUniqueId(iterText, iterUniqueId);
                    var lc = ExpandTextInline(iterText, output, iterCtx, forNode.FullSpan,
                        TextReplayReason.ForTokenShapingSubstitution);
                    if (lc == LoopControl.Break)
                    {
                        _conditional.ResetToDepth(condDepthBefore);
                        break;
                    }
                }
                if (_conditional.Depth != condDepthBefore)
                    _diagnostics.Report(forNode.FullSpan, "Unbalanced IF/ENDC inside FOR body");
            }
        }
        else
        {
            // No variable name — structural replay is always safe (nothing to substitute)
            ExpandForStructural(forBodyNodes, varName, start, stop, step,
                forNode, output, ctx);
        }
    }

    // =========================================================================
    // Structural replay (no text substitution, no SyntaxTree.Parse per iteration)
    // =========================================================================

    /// <summary>
    /// Walk parsed body nodes N times without text extraction or re-parsing.
    /// Used when REPT body contains no \@ (structural replay classification).
    /// Returns LoopControl.Break if BREAK was encountered.
    /// </summary>
    private LoopControl ExpandReptStructural(List<SyntaxNodeOrToken> bodyNodes, int count,
        SyntaxNode reptNode, List<ExpandedNode> output, ExpansionContext ctx)
    {
        var condDepthBefore = _conditional.Depth;
        for (int iter = 0; iter < count; iter++)
        {
            _uniqueIdCounter++;
            var iterUniqueId = _uniqueIdCounter;
            var iterFrame = ExpansionFrame.ForRept(ctx.FilePath, reptNode.FullSpan, iter);
            var iterCtx = ctx.ForLoop(iterFrame, iterUniqueId);

            int j = 0;
            var lc = ExpandBodyList(bodyNodes, ref j, output, iterCtx);
            if (lc == LoopControl.Break)
            {
                _conditional.ResetToDepth(condDepthBefore);
                return LoopControl.Break;
            }
        }
        if (_conditional.Depth != condDepthBefore)
            _diagnostics.Report(reptNode.FullSpan, "Unbalanced IF/ENDC inside REPT body");
        return LoopControl.Continue;
    }

    /// <summary>
    /// Walk parsed body nodes for each FOR iteration. If a variable is named, emits a synthetic
    /// REDEF node before each body so that the Binder's Pass 1 and Pass 2 both see the correct
    /// per-iteration value without re-parsing the body. No text substitution or per-body reparse.
    /// </summary>
    private LoopControl ExpandForStructural(List<SyntaxNodeOrToken> bodyNodes,
        string? varName, long start, long stop, long step,
        SyntaxNode forNode, List<ExpandedNode> output, ExpansionContext ctx)
    {
        var condDepthBefore = _conditional.Depth;
        int iterIndex = 0;
        for (long v = start; step > 0 ? v < stop : v > stop; v += step, iterIndex++)
        {
            _uniqueIdCounter++;
            var iterUniqueId = _uniqueIdCounter;
            var iterFrame = ExpansionFrame.ForFor(ctx.FilePath, forNode.FullSpan, varName, iterIndex);
            var iterCtx = ctx.ForLoop(iterFrame, iterUniqueId);

            if (varName != null)
            {
                _symbols.DefineOrRedefine(varName, v, _ownerContext);
                // Emit a synthetic REDEF node so Pass 1 and Pass 2 re-evaluate the symbol.
                // Uses iterCtx.Trace (the iteration context), not ctx.Trace.
                var synthNode = BuildSyntheticRedef(varName, v);
                output.Add(new ExpandedNode(synthNode, iterCtx.FilePath,
                    _conditional.Depth > 0, iterCtx.MacroBodyDepth > 0, iterCtx.Trace));
            }

            int j = 0;
            var lc = ExpandBodyList(bodyNodes, ref j, output, iterCtx);
            if (lc == LoopControl.Break)
            {
                _conditional.ResetToDepth(condDepthBefore);
                return LoopControl.Break;
            }
        }
        if (_conditional.Depth != condDepthBefore)
            _diagnostics.Report(forNode.FullSpan, "Unbalanced IF/ENDC inside FOR body");
        return LoopControl.Continue;
    }

    // =========================================================================
    // Synthetic node construction
    // =========================================================================

    /// <summary>
    /// Build a synthetic REDEF node (SymbolDirective) from green nodes directly,
    /// avoiding SyntaxTree.Parse per loop iteration. Constructs:
    /// REDEF {varName} EQU {value}
    /// </summary>
    private static SyntaxNode BuildSyntheticRedef(string varName, long value)
    {
        var redefToken = new GreenToken(SyntaxKind.RedefKeyword, "REDEF",
            trailingTrivia: [new GreenTrivia(SyntaxKind.WhitespaceTrivia, " ")]);
        var nameToken = new GreenToken(SyntaxKind.IdentifierToken, varName,
            trailingTrivia: [new GreenTrivia(SyntaxKind.WhitespaceTrivia, " ")]);
        var equToken = new GreenToken(SyntaxKind.EquKeyword, "EQU",
            trailingTrivia: [new GreenTrivia(SyntaxKind.WhitespaceTrivia, " ")]);
        var valueToken = new GreenToken(SyntaxKind.NumberLiteral, value.ToString());
        var valueExpr = new GreenNode(SyntaxKind.LiteralExpression, [valueToken]);
        var directive = new GreenNode(SyntaxKind.SymbolDirective,
            [redefToken, nameToken, equToken, valueExpr]);
        return new SyntaxNode(directive, null, 0);
    }

    // =========================================================================
    // String interpolation — delegated to InterpolationResolver
    // =========================================================================

    /// <summary>Current section name for SECTION(@) function resolution.</summary>
    internal string? CurrentSectionName
    {
        get => _interpolation.CurrentSectionName;
        set => _interpolation.CurrentSectionName = value;
    }

    /// <summary>
    /// Resolve {symbol} and {fmt:symbol} interpolations in a string.
    /// Delegates to <see cref="InterpolationResolver"/>.
    /// </summary>
    internal string ResolveInterpolations(string text) => _interpolation.Resolve(text);

    /// <summary>
    /// Parse and expand text inline (used for macro/REPT/FOR text-level expansion).
    /// </summary>
    private LoopControl ExpandTextInline(string text, List<ExpandedNode> output,
        ExpansionContext ctx, TextSpan triggerSpan, TextReplayReason reason)
    {
        // Resolve {symbol} interpolations before re-parsing, but ONLY if there are no
        // unresolved macro parameter references (\1..\9, \#) in the text. When macro params
        // are still present (SHIFT deferred them), interpolations inside string literals like
        // PRINTLN "{d:\1}" must NOT be resolved yet — \1 is not a symbol name. The per-node
        // NeedsInterpolation path in ExpandBodyList will resolve them after macro params are
        // substituted via the ContainsMacroParam path.
        bool hasMacroParams = TextReplayService.ContainsUnresolvedMacroParam(text);

        var tree = _textReplay.ParseForReplay(text, hasMacroParams, ctx, triggerSpan, reason, MaxReplayDepth);
        if (tree == null)
            return LoopControl.Continue;

        var replayCtx = ctx.ForTextReplay(tree.Text, triggerSpan, reason);

        var children = tree.Root.ChildNodesAndTokens().ToList();
        int j = 0;
        return ExpandBodyList(children, ref j, output, replayCtx);
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

    // =========================================================================
    // Green tree inspection helpers
    // =========================================================================

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
            // \1..\9, \#, _NARG may also appear literally inside a string literal token
            // (e.g. PRINTLN "\1s!" — the lexer embeds \1 in the StringLiteral text).
            // We must detect them here so the lazy resolution path fires even for PRINTLN.
            if (tok.Kind == SyntaxKind.StringLiteral && StringLiteralContainsMacroParam(tok.Text))
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
    /// Returns true if the text of a StringLiteral token contains any macro parameter
    /// reference: \1..\9, \#, or _NARG (case-insensitive).
    /// </summary>
    private static bool StringLiteralContainsMacroParam(string tokenText)
    {
        if (!tokenText.Contains('\\') && !tokenText.Contains("_NARG", StringComparison.OrdinalIgnoreCase))
            return false;
        for (int i = 0; i < tokenText.Length - 1; i++)
        {
            if (tokenText[i] == '\\')
            {
                char next = tokenText[i + 1];
                if (next >= '1' && next <= '9') return true;
                if (next == '#') return true;
            }
        }
        return tokenText.IndexOf("_NARG", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Resolve \1..\9, \#, _NARG in raw text against the current macro frame.
    /// Used for lazy resolution when SHIFT is active.
    /// Reports an error if \N references an arg that has been shifted past.
    /// </summary>
    private string ResolveMacroParamsInText(string text, MacroFrame frame)
        => _textReplay.SubstituteParamReferences(text, frame, reportShiftedPast: true,
            _symbols, _expressionCache);

    /// <summary>
    /// Recursively find the text of the first token with the given kind in the subtree.
    /// Returns null if not found.
    /// </summary>
    private static string? FindTokenInSubtree(GreenNodeBase green, SyntaxKind kind)
    {
        if (green is GreenToken tok)
            return tok.Kind == kind ? tok.Text : null;
        for (int i = 0; i < green.ChildCount; i++)
        {
            var child = green.GetChild(i);
            if (child == null) continue;
            var found = FindTokenInSubtree(child, kind);
            if (found != null) return found;
        }
        return null;
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

    /// <summary>
    /// Returns true if the node has more than <paramref name="threshold"/> direct child tokens.
    /// Exits early once the threshold is exceeded — no full enumeration needed.
    /// </summary>
    private static bool HasChildTokensBeyond(SyntaxNode node, int threshold)
    {
        int count = 0;
        foreach (var _ in node.ChildTokens())
        {
            count++;
            if (count > threshold) return true;
        }
        return false;
    }

}
