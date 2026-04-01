using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Binding;

/// <summary>
/// A collected macro definition with pre-analyzed body properties and a pre-parsed syntax tree.
///
/// The pre-parsed tree enables two key optimizations:
/// 1. Macros without parameter references skip text substitution + reparse entirely
///    (the pre-parsed tree is replayed directly through the expansion kernel).
/// 2. SHIFT detection uses the parsed token stream rather than a text scan, avoiding
///    false positives from "SHIFT" inside string literals or comments.
///
/// The raw body text is retained for macros that DO require text-level substitution
/// (\1..\9, \@, \#, \&lt;expr&gt;, _NARG) — these still go through the text-rewrite path
/// because RGBDS parameter substitution is a text-level operation that can create new
/// tokens by concatenation (e.g., label\@ → label_1).
/// </summary>
internal sealed class MacroDefinition
{
    /// <summary>Macro name (case-insensitive matching is handled by the dictionary in AssemblyExpander).</summary>
    public string Name { get; }

    /// <summary>Raw body text extracted from source. Used for text-level substitution when needed.</summary>
    public string RawBody { get; }

    /// <summary>
    /// Pre-parsed syntax tree of the body. Used for fast-path expansion (no param references)
    /// and for structural analysis (SHIFT detection).
    /// </summary>
    public SyntaxTree ParsedBody { get; }

    /// <summary>
    /// True if the body contains \1..\9, \@, \#, \&lt;expr&gt;, or _NARG references
    /// that require text-level substitution before expansion. When false, the pre-parsed
    /// tree can be replayed directly.
    /// </summary>
    public bool RequiresTextSubstitution { get; }

    /// <summary>
    /// True if the body contains an actual SHIFT keyword token (not "SHIFT" in a string
    /// literal or comment). When true, \1..\9 and \# are left as MacroParamTokens for
    /// lazy resolution so SHIFT can mutate the argument window.
    /// </summary>
    public bool ContainsShift { get; }

    /// <summary>Span of the MACRO directive in the definition file.</summary>
    public TextSpan DefinitionSpan { get; }

    /// <summary>File path where the macro was defined.</summary>
    public string DefinitionFilePath { get; }

    public MacroDefinition(string name, string rawBody, TextSpan definitionSpan, string definitionFilePath)
    {
        Name = name;
        RawBody = rawBody;
        DefinitionSpan = definitionSpan;
        DefinitionFilePath = definitionFilePath;
        ParsedBody = SyntaxTree.Parse(rawBody);
        RequiresTextSubstitution = ScanForParamReferences(rawBody);
        ContainsShift = ScanForShiftToken(ParsedBody.Root.Green);
    }

    /// <summary>
    /// Scan raw text for macro parameter references: \1..\9, \@, \#, \&lt;expr&gt;, _NARG.
    /// Any of these require text-level substitution before expansion.
    /// </summary>
    private static bool ScanForParamReferences(string text)
    {
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '\\')
            {
                char next = text[i + 1];
                if (next is >= '1' and <= '9') return true;
                if (next == '@') return true;
                if (next == '#') return true;
                if (next == '<') return true;
            }
        }
        return text.Contains("_NARG", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Walk the pre-parsed green tree looking for an actual ShiftKeyword token.
    /// More precise than <c>text.Contains("SHIFT")</c> — won't match inside string literals or comments.
    /// </summary>
    private static bool ScanForShiftToken(GreenNodeBase green)
    {
        if (green is GreenToken token)
            return token.Kind == SyntaxKind.ShiftKeyword;
        for (int i = 0; i < green.ChildCount; i++)
        {
            var child = green.GetChild(i);
            if (child != null && ScanForShiftToken(child))
                return true;
        }
        return false;
    }
}
