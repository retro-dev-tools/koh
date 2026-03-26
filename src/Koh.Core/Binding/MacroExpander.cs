using Koh.Core.Diagnostics;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Binding;

/// <summary>
/// Collects macro definitions and expands macro calls via text-level substitution.
/// Macro bodies are stored as raw source text; expansion substitutes \1..\9 and \@,
/// then re-parses the result into a SyntaxTree whose nodes are bound inline.
/// </summary>
internal sealed class MacroExpander
{
    private readonly Dictionary<string, MacroDefinition> _macros = new(StringComparer.OrdinalIgnoreCase);
    private readonly DiagnosticBag _diagnostics;
    private int _invocationCounter;

    public MacroExpander(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Scan the tree for MACRO/ENDM blocks and register macro definitions.
    /// Call this before Pass 1.
    /// </summary>
    public void CollectDefinitions(SyntaxNode root, SourceText source)
    {
        var children = root.ChildNodesAndTokens().ToList();

        for (int i = 0; i < children.Count; i++)
        {
            if (!children[i].IsNode) continue;
            var node = children[i].AsNode!;

            // Pattern: LabelDeclaration (macro name) followed by MacroDefinition (MACRO keyword)
            if (node.Kind == SyntaxKind.LabelDeclaration && i + 1 < children.Count)
            {
                var next = children[i + 1];
                if (next.IsNode && next.AsNode!.Kind == SyntaxKind.MacroDefinition)
                {
                    var macroKeyword = next.AsNode!.ChildTokens().FirstOrDefault();
                    if (macroKeyword?.Kind == SyntaxKind.MacroKeyword)
                    {
                        var nameToken = node.ChildTokens().First();
                        var name = nameToken.Text;

                        // Find matching ENDM
                        int bodyStart = -1;
                        int bodyEnd = -1;
                        int endmIndex = -1;

                        // Body starts after the MACRO line
                        bodyStart = next.AsNode!.FullSpan.Start + next.AsNode!.FullSpan.Length;

                        for (int j = i + 2; j < children.Count; j++)
                        {
                            if (!children[j].IsNode) continue;
                            var candidate = children[j].AsNode!;
                            if (candidate.Kind == SyntaxKind.MacroDefinition)
                            {
                                var kw = candidate.ChildTokens().FirstOrDefault();
                                if (kw?.Kind == SyntaxKind.EndmKeyword)
                                {
                                    bodyEnd = candidate.Position;
                                    endmIndex = j;
                                    break;
                                }
                            }
                        }

                        if (endmIndex >= 0)
                        {
                            var bodyText = source.ToString(new TextSpan(bodyStart, bodyEnd - bodyStart)).Trim();
                            _macros[name] = new MacroDefinition(name, bodyText);
                            i = endmIndex; // skip past ENDM
                        }
                        else
                        {
                            _diagnostics.Report(node.FullSpan,
                                $"Macro '{name}' has no matching ENDM");
                        }
                    }
                }
            }
        }
    }

    /// <summary>Check if a name is a known macro.</summary>
    public bool IsMacro(string name) => _macros.ContainsKey(name);

    /// <summary>
    /// Expand a macro call. Returns a parsed SyntaxTree of the expanded body,
    /// or null if the macro is not found.
    /// </summary>
    public SyntaxTree? Expand(string name, IReadOnlyList<string> arguments)
    {
        if (!_macros.TryGetValue(name, out var macro))
            return null;

        _invocationCounter++;
        var body = macro.Body;

        // Substitute \1..\9 with arguments
        for (int i = 0; i < 9; i++)
        {
            var placeholder = $"\\{i + 1}";
            var replacement = i < arguments.Count ? arguments[i] : "";
            body = body.Replace(placeholder, replacement);
        }

        // Substitute \@ with unique suffix
        body = body.Replace("\\@", $"_{_invocationCounter}");

        // Substitute _NARG
        body = body.Replace("_NARG", arguments.Count.ToString());

        return SyntaxTree.Parse(body);
    }

    private sealed record MacroDefinition(string Name, string Body);
}
