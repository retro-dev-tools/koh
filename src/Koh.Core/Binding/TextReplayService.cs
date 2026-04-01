using System.Text.RegularExpressions;
using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Binding;

/// <summary>
/// Encapsulates all text-level substitution and replay logic used by <see cref="AssemblyExpander"/>.
/// Handles macro parameter substitution, FOR variable substitution, unique-ID injection,
/// and the text-replay pipeline (text → SyntaxTree.Parse with depth guard).
/// </summary>
internal sealed class TextReplayService
{
    private readonly DiagnosticBag _diagnostics;
    private readonly InterpolationResolver _interpolation;

    // Pre-compiled pattern that matches either a double-quoted string literal or any
    // non-string-literal content (captured separately). Used by SubstituteOutsideStrings.
    internal static readonly Regex StringLiteralSplitter =
        new Regex(@"""(?:[^""\\]|\\.)*""|[^""]+", RegexOptions.Compiled);

    internal static readonly Regex NargPattern =
        new Regex(@"\b_NARG\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public TextReplayService(DiagnosticBag diagnostics, InterpolationResolver interpolation)
    {
        _diagnostics = diagnostics;
        _interpolation = interpolation;
    }

    // =========================================================================
    // Replay entry point
    // =========================================================================

    /// <summary>
    /// Resolve interpolations (unless macro params are still present), enforce replay depth,
    /// and parse the text into a SyntaxTree for expansion. Returns null if the depth limit
    /// is exceeded (a diagnostic is emitted).
    /// </summary>
    public SyntaxTree? ParseForReplay(string text, bool hasMacroParams,
        ExpansionContext ctx, TextSpan triggerSpan, TextReplayReason reason,
        int maxReplayDepth)
    {
        if (!hasMacroParams)
            text = _interpolation.Resolve(text);

        if (ctx.ReplayDepth >= maxReplayDepth)
        {
            _diagnostics.Report(triggerSpan,
                $"Maximum text replay depth ({maxReplayDepth}) exceeded");
            return null;
        }

        return SyntaxTree.Parse(text);
    }

    // =========================================================================
    // Unique-ID substitution
    // =========================================================================

    /// <summary>Replace all occurrences of \@ in <paramref name="bodyText"/> with <c>_{uniqueId}</c>.</summary>
    public static string SubstituteUniqueId(string bodyText, int uniqueId)
        => bodyText.Replace("\\@", $"_{uniqueId}");

    // =========================================================================
    // Macro parameter substitution
    // =========================================================================

    /// <summary>
    /// Substitute macro params in body text. Only \@ is eagerly baked (immutable per invocation).
    /// \1..\9, \#, _NARG are resolved lazily via MacroFrame when SHIFT is present;
    /// otherwise eagerly substituted for efficiency.
    /// </summary>
    public string SubstituteMacroParams(string body, MacroFrame frame, bool containsShift,
        SymbolTable symbols, Dictionary<string, GreenNodeBase?> expressionCache)
    {
        // \@ → unique suffix per invocation (eagerly baked — immutable)
        body = SubstituteUniqueId(body, frame.UniqueId);

        if (containsShift)
        {
            // Bodies with SHIFT leave \1..\9, \#, _NARG as-is. They survive as
            // MacroParamToken in the re-parsed tree and are resolved lazily from
            // the MacroFrame during ExpandBodyList. This allows SHIFT to mutate
            // the argument window and affect subsequent references.
            return body;
        }

        // No SHIFT — eagerly substitute everything (faster, no MacroParamToken overhead)
        return SubstituteParamReferences(body, frame, reportShiftedPast: false,
            symbols, expressionCache);
    }

    /// <summary>
    /// Shared core for macro parameter substitution: \1..\9, \&lt;expr&gt;, \#, _NARG.
    /// When <paramref name="reportShiftedPast"/> is true, reports a diagnostic for
    /// \N references that have been shifted past the argument list (lazy/SHIFT path).
    /// When false, silently substitutes "" for missing args (eager/no-SHIFT path).
    /// </summary>
    public string SubstituteParamReferences(string text, MacroFrame frame, bool reportShiftedPast,
        SymbolTable symbols, Dictionary<string, GreenNodeBase?> expressionCache)
    {
        for (int p = 9; p >= 1; p--)
        {
            var placeholder = $"\\{p}";
            if (!text.Contains(placeholder)) continue;

            if (reportShiftedPast)
            {
                int argIndex = p - 1 + frame.ShiftOffset;
                if (argIndex >= frame.Args.Count)
                {
                    _diagnostics.Report(default, $"Macro argument \\{p} not defined (shifted past end)");
                    text = text.Replace(placeholder, "");
                    continue;
                }
            }

            text = text.Replace(placeholder, frame.GetArg(p));
        }

        text = ResolveComputedArgs(text, frame, symbols, expressionCache);

        if (text.Contains("\\#"))
            text = text.Replace("\\#", frame.AllArgs());

        text = SubstituteOutsideStrings(text, NargPattern, frame.Narg.ToString());

        return text;
    }

    /// <summary>
    /// Resolve \&lt;expr&gt; computed arg index references in macro body text.
    /// The expr is evaluated as an integer and used as a 1-based argument index.
    /// </summary>
    public string ResolveComputedArgs(string body, MacroFrame frame,
        SymbolTable symbols, Dictionary<string, GreenNodeBase?> expressionCache)
    {
        int searchFrom = 0;
        while (true)
        {
            int start = body.IndexOf("\\<", searchFrom, StringComparison.Ordinal);
            if (start < 0) break;
            int end = body.IndexOf('>', start + 2);
            if (end < 0) break;

            var exprText = body[(start + 2)..end];
            var evaluator = new ExpressionEvaluator(symbols, _diagnostics, () => 0);
            var exprGreen = ParseExpressionCached(exprText, expressionCache);
            long? val = exprGreen != null ? evaluator.TryEvaluate(exprGreen) : null;

            if (val.HasValue)
            {
                var replacement = frame.GetArg((int)val.Value);
                body = body[..start] + replacement + body[(end + 1)..];
                searchFrom = start + replacement.Length;
            }
            else
            {
                _diagnostics.Report(default,
                    $"Invalid computed macro argument expression: \\<{exprText}>");
                searchFrom = end + 1;
            }
        }
        return body;
    }

    // =========================================================================
    // Outside-string substitution
    // =========================================================================

    /// <summary>
    /// Replace all occurrences of <paramref name="pattern"/> in <paramref name="text"/>
    /// with <paramref name="replacement"/>, but only in regions that are not inside
    /// double-quoted string literals. String literal content is preserved verbatim.
    /// </summary>
    public static string SubstituteOutsideStrings(string text, Regex pattern, string replacement)
    {
        if (!text.Contains('"'))
            return pattern.Replace(text, replacement); // fast path: no string literals

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (Match m in StringLiteralSplitter.Matches(text))
        {
            var segment = m.Value;
            if (segment.Length > 0 && segment[0] == '"')
                sb.Append(segment);
            else
                sb.Append(pattern.Replace(segment, replacement));
        }
        return sb.ToString();
    }

    // =========================================================================
    // FOR variable substitution
    // =========================================================================

    /// <summary>
    /// Extract the source text of the loop body from the header node and body node list.
    /// </summary>
    public static string ExtractBodyText(SyntaxNode headerNode,
        IReadOnlyList<SyntaxNodeOrToken> body, ExpansionContext ctx)
    {
        if (ctx.SourceText == null || body.Count == 0)
            return "";

        int bodyTextStart = headerNode.FullSpan.Start + headerNode.FullSpan.Length;
        int bodyTextEnd = bodyTextStart;

        var last = body[^1];
        if (last.IsNode)
            bodyTextEnd = last.AsNode!.FullSpan.Start + last.AsNode!.FullSpan.Length;
        else if (last.IsToken)
            bodyTextEnd = last.AsToken!.FullSpan.Start + last.AsToken!.FullSpan.Length;

        return ctx.SourceText.ToString(new TextSpan(bodyTextStart, bodyTextEnd - bodyTextStart));
    }

    /// <summary>
    /// Recursively collect the <see cref="TextSpan"/> positions of all IdentifierToken nodes
    /// in the red tree that match <paramref name="name"/> (case-insensitive).
    /// </summary>
    public static void CollectIdentifierPositions(SyntaxNode node, string name,
        List<(int Start, int Length)> positions)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsToken)
            {
                var token = child.AsToken!;
                if (token.Kind == SyntaxKind.IdentifierToken &&
                    token.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    positions.Add((token.Span.Start, token.Span.Length));
                }
            }
            else if (child.IsNode)
            {
                CollectIdentifierPositions(child.AsNode!, name, positions);
            }
        }
    }

    /// <summary>
    /// Replace text at pre-computed token positions. Accounts for cumulative offset from
    /// prior replacements.
    /// </summary>
    public static string SubstituteAtPositions(string source,
        List<(int Start, int Length)> positions, string replacement)
    {
        if (positions.Count == 0) return source;

        var sb = new System.Text.StringBuilder(source.Length);
        int pos = 0;
        foreach (var (start, length) in positions)
        {
            sb.Append(source, pos, start - pos);
            sb.Append(replacement);
            pos = start + length;
        }
        sb.Append(source, pos, source.Length - pos);
        return sb.ToString();
    }

    // =========================================================================
    // Detection helpers
    // =========================================================================

    /// <summary>
    /// Returns true if the raw source text contains any unresolved macro parameter reference:
    /// \1..\9 or \# anywhere in the text.
    /// </summary>
    public static bool ContainsUnresolvedMacroParam(string text)
    {
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '\\')
            {
                char next = text[i + 1];
                if (next >= '1' && next <= '9') return true;
                if (next == '#') return true;
            }
        }
        return false;
    }

    // =========================================================================
    // Body classification
    // =========================================================================

    /// <summary>
    /// Classify a REPT body: if it contains \@ it needs text replay for unique-label substitution,
    /// otherwise it can be replayed structurally.
    /// </summary>
    public static BodyReplayPlan ClassifyReptBody(string bodyText)
    {
        return bodyText.Contains("\\@")
            ? new BodyReplayPlan(BodyReplayKind.RequiresTextReplay, TextReplayReason.UniqueLabelSubstitution)
            : new BodyReplayPlan(BodyReplayKind.Structural);
    }

    /// <summary>
    /// Classify a FOR body. Conservative first pass: if contains \@ or unresolved macro params,
    /// requires text replay. Otherwise parse once and check whether every reference to varName
    /// is an IdentifierToken — if any reference appears in a non-identifier position (e.g. inside
    /// a string literal), text replay is required for correct substitution.
    /// </summary>
    public static BodyReplayPlan ClassifyForBody(string bodyText, string varName)
    {
        // Quick exits — obvious text-replay triggers (no positions needed for \@ or macro params)
        if (bodyText.Contains("\\@"))
            return new BodyReplayPlan(BodyReplayKind.RequiresTextReplay, TextReplayReason.UniqueLabelSubstitution);

        if (ContainsUnresolvedMacroParam(bodyText))
            return new BodyReplayPlan(BodyReplayKind.RequiresTextReplay, TextReplayReason.MacroParameterConcatenation);

        // One-time parse to check token shape — reuse for position collection if text replay needed
        var tree = SyntaxTree.Parse(bodyText);
        bool allIdentifiers = CheckAllVarRefsAreIdentifiers(tree.Root, varName);
        if (allIdentifiers)
            return new BodyReplayPlan(BodyReplayKind.Structural);

        // Text replay needed — collect identifier positions from this same parse to avoid reparsing
        var positions = new List<(int Start, int Length)>();
        CollectIdentifierPositions(tree.Root, varName, positions);
        return new BodyReplayPlan(BodyReplayKind.RequiresTextReplay,
            TextReplayReason.ForTokenShapingSubstitution, positions);
    }

    /// <summary>
    /// Walk the parsed tree via ChildNodesAndTokens() recursively and verify that every
    /// token whose text equals varName is an IdentifierToken. If any such token has a
    /// different kind, the variable name cannot be safely substituted at the structural level.
    /// </summary>
    private static bool CheckAllVarRefsAreIdentifiers(SyntaxNode node, string varName)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsToken)
            {
                var tok = child.AsToken!;
                // Skip string literal content — the variable won't be substituted there anyway
                if (tok.Kind == SyntaxKind.StringLiteral) continue;
                if (tok.Text.Equals(varName, StringComparison.OrdinalIgnoreCase) &&
                    tok.Kind != SyntaxKind.IdentifierToken)
                    return false;
            }
            else if (child.IsNode)
            {
                if (!CheckAllVarRefsAreIdentifiers(child.AsNode!, varName))
                    return false;
            }
        }
        return true;
    }

    // =========================================================================
    // Expression cache helper
    // =========================================================================

    private static GreenNodeBase? ParseExpressionCached(string exprText,
        Dictionary<string, GreenNodeBase?> cache)
    {
        if (cache.TryGetValue(exprText, out var cached))
            return cached;
        var tree = SyntaxTree.Parse(exprText);
        var exprNode = tree.Root.ChildNodes().FirstOrDefault()?.Green;
        cache[exprText] = exprNode;
        return exprNode;
    }
}
