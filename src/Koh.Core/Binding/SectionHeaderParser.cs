using Koh.Core.Diagnostics;
using Koh.Core.Syntax;

namespace Koh.Core.Binding;

/// <summary>
/// Parses SECTION directive nodes to extract name, type, fixed address, and bank.
/// Stateless — can be called from both Pass 1 and Pass 2.
/// </summary>
internal static class SectionHeaderParser
{
    /// <summary>
    /// Extract section header fields from a SectionDirective node.
    /// Returns false and reports a diagnostic if the name is absent.
    /// </summary>
    public static bool TryParse(SyntaxNode node, DiagnosticBag diagnostics,
        out string? name, out SectionType sectionType,
        out int? fixedAddress, out int? bank,
        out bool isUnion, out bool isFragment,
        out int? sectionAlignBits, out int? sectionAlignOffset)
    {
        name = null;
        sectionType = SectionType.Rom0;
        fixedAddress = null;
        bank = null;
        isUnion = false;
        isFragment = false;
        sectionAlignBits = null;
        sectionAlignOffset = null;

        var tokens = node.ChildTokens().ToList();
        SyntaxKind? lastKeyword = null;

        // Check for UNION/FRAGMENT modifiers
        for (int j = 0; j < tokens.Count; j++)
        {
            if (tokens[j].Kind == SyntaxKind.UnionKeyword) isUnion = true;
            if (tokens[j].Kind == SyntaxKind.FragmentKeyword) isFragment = true;
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            if (t.Kind == SyntaxKind.StringLiteral)
            {
                name = t.Text.Length >= 2 ? t.Text[1..^1] : t.Text;
                continue;
            }

            var mapped = t.Kind switch
            {
                SyntaxKind.Rom0Keyword  => SectionType.Rom0,
                SyntaxKind.RomxKeyword  => SectionType.RomX,
                SyntaxKind.Wram0Keyword => SectionType.Wram0,
                SyntaxKind.WramxKeyword => SectionType.WramX,
                SyntaxKind.VramKeyword  => SectionType.Vram,
                SyntaxKind.HramKeyword  => SectionType.Hram,
                SyntaxKind.SramKeyword  => SectionType.Sram,
                SyntaxKind.OamKeyword   => SectionType.Oam,
                _ => (SectionType?)null,
            };
            if (mapped.HasValue)
            {
                sectionType = mapped.Value;
                lastKeyword = t.Kind;
                continue;
            }

            if (t.Kind == SyntaxKind.OpenBracketToken)
            {
                // Collect all tokens up to matching CloseBracket
                int closeIdx = -1;
                for (int j = i + 1; j < tokens.Count; j++)
                {
                    if (tokens[j].Kind == SyntaxKind.CloseBracketToken) { closeIdx = j; break; }
                }
                if (closeIdx > i)
                {
                    var innerTokens = tokens.Skip(i + 1).Take(closeIdx - i - 1).ToList();

                    if (lastKeyword == SyntaxKind.AlignKeyword)
                    {
                        // ALIGN[bits[, offset]] — parse as comma-separated integer literals
                        var nums = new List<int>();
                        foreach (var tok in innerTokens)
                        {
                            if (tok.Kind == SyntaxKind.NumberLiteral &&
                                TryParseIntegerLiteral(tok.Text, out int n))
                                nums.Add(n);
                        }
                        if (nums.Count > 0) sectionAlignBits = nums[0];
                        if (nums.Count > 1) sectionAlignOffset = nums[1];
                    }
                    else
                    {
                        // Evaluate the bracket expression: supports simple integer arithmetic.
                        // This catches div-by-zero in bank expressions like ROMX[1/0].
                        var result = EvaluateBracketExpression(innerTokens, diagnostics);
                        if (result.HasValue)
                        {
                            if (lastKeyword == SyntaxKind.BankKeyword)
                                bank = (int)result.Value;
                            else
                                fixedAddress = (int)result.Value;
                        }
                    }
                    i = closeIdx;
                }
                continue;
            }

            if (t.Kind == SyntaxKind.BankKeyword)
            {
                lastKeyword = SyntaxKind.BankKeyword;
                continue;
            }

            if (t.Kind == SyntaxKind.AlignKeyword)
            {
                lastKeyword = SyntaxKind.AlignKeyword;
                continue;
            }
        }

        if (name != null) return true;

        diagnostics.Report(node.FullSpan, "SECTION directive requires a name");
        return false;
    }

    /// <summary>
    /// Parse a GB assembler integer literal: hex ($xxxx), binary (%bbb), or decimal.
    /// </summary>
    public static bool TryParseIntegerLiteral(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        if (text[0] == '$')
            return int.TryParse(text[1..], System.Globalization.NumberStyles.HexNumber,
                null, out value);
        if (text[0] == '%')
        {
            try { value = Convert.ToInt32(text[1..], 2); return true; }
            catch { return false; }
        }
        return int.TryParse(text, out value);
    }

    /// <summary>
    /// Evaluate a sequence of tokens (the content of a bracket group) as a simple integer
    /// arithmetic expression. Supports literals, +, -, *, /, %. Reports division-by-zero.
    /// Uses a recursive descent approach for correct precedence (left-associative, * / % before + -).
    /// Returns null on error (bad token or div-by-zero).
    /// </summary>
    private static int? EvaluateBracketExpression(IReadOnlyList<SyntaxToken> tokens, DiagnosticBag diagnostics)
    {
        if (tokens.Count == 0) return null;

        // Single token: a number literal
        if (tokens.Count == 1)
        {
            if (tokens[0].Kind == SyntaxKind.NumberLiteral && TryParseIntegerLiteral(tokens[0].Text, out int v))
                return v;
            return null;
        }

        // Find the lowest-precedence binary operator (rightmost at each precedence level)
        // to split the expression. Scan left-to-right; the last +/- we find at depth 0 is
        // the split point for additive; if none, find the last * / % for multiplicative.
        int addIdx = -1;
        int mulIdx = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            var kind = tokens[i].Kind;
            if (kind is SyntaxKind.PlusToken or SyntaxKind.MinusToken)
                addIdx = i;
            else if (kind is SyntaxKind.StarToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken)
                mulIdx = i;
        }

        int splitIdx = addIdx >= 0 ? addIdx : mulIdx;
        if (splitIdx <= 0 || splitIdx >= tokens.Count - 1) return null; // no operator found at valid position

        var left = EvaluateBracketExpression(tokens.Take(splitIdx).ToList(), diagnostics);
        var right = EvaluateBracketExpression(tokens.Skip(splitIdx + 1).ToList(), diagnostics);
        if (left == null || right == null) return null;

        return tokens[splitIdx].Kind switch
        {
            SyntaxKind.PlusToken    => left.Value + right.Value,
            SyntaxKind.MinusToken   => left.Value - right.Value,
            SyntaxKind.StarToken    => left.Value * right.Value,
            SyntaxKind.SlashToken when right.Value != 0 => left.Value / right.Value,
            SyntaxKind.SlashToken   => ReportDivByZero(diagnostics),
            SyntaxKind.PercentToken when right.Value != 0 => left.Value % right.Value,
            SyntaxKind.PercentToken => ReportDivByZero(diagnostics),
            _ => null,
        };
    }

    private static int? ReportDivByZero(DiagnosticBag diagnostics)
    {
        diagnostics.Report(default, "Division by zero in section bank expression");
        return null;
    }
}
