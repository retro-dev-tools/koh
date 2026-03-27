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
        out bool isUnion, out bool isFragment)
    {
        name = null;
        sectionType = SectionType.Rom0;
        fixedAddress = null;
        bank = null;
        isUnion = false;
        isFragment = false;

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
                if (i + 2 < tokens.Count &&
                    tokens[i + 1].Kind == SyntaxKind.NumberLiteral &&
                    tokens[i + 2].Kind == SyntaxKind.CloseBracketToken)
                {
                    if (TryParseIntegerLiteral(tokens[i + 1].Text, out int value))
                    {
                        if (lastKeyword == SyntaxKind.BankKeyword)
                            bank = value;
                        else
                            fixedAddress = value;
                    }
                    i += 2;
                }
                continue;
            }

            if (t.Kind == SyntaxKind.BankKeyword)
            {
                lastKeyword = SyntaxKind.BankKeyword;
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
}
