using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Binding;

/// <summary>
/// Folds constant expressions. Returns null for forward references or
/// linker-time symbols (BANK, SIZEOF, STARTOF).
/// </summary>
public sealed class ExpressionEvaluator
{
    private readonly SymbolTable _symbols;
    private readonly DiagnosticBag _diagnostics;
    private readonly Func<int> _getCurrentPC;

    public ExpressionEvaluator(SymbolTable symbols, DiagnosticBag diagnostics,
        Func<int> getCurrentPC)
    {
        _symbols = symbols;
        _diagnostics = diagnostics;
        _getCurrentPC = getCurrentPC;
    }

    public long? TryEvaluate(GreenNodeBase node)
    {
        return node.Kind switch
        {
            SyntaxKind.LiteralExpression => EvaluateLiteral(node),
            SyntaxKind.NameExpression => EvaluateName(node),
            SyntaxKind.BinaryExpression => EvaluateBinary(node),
            SyntaxKind.UnaryExpression => EvaluateUnary(node),
            SyntaxKind.ParenthesizedExpression => EvaluateParenthesized(node),
            SyntaxKind.FunctionCallExpression => EvaluateFunction(node),
            // Raw tokens (e.g., from LabelOperand or ImmediateOperand unwrapping)
            SyntaxKind.NumberLiteral => ParseNumber(((GreenToken)node).Text),
            SyntaxKind.CurrentAddressToken => _getCurrentPC(),
            SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken =>
                EvaluateRawIdentifier(((GreenToken)node).Text),
            SyntaxKind.StringLiteral => null,
            _ => null,
        };
    }

    private long? EvaluateLiteral(GreenNodeBase node)
    {
        var token = (GreenToken)((GreenNode)node).GetChild(0)!;
        return token.Kind switch
        {
            SyntaxKind.NumberLiteral => ParseNumber(token.Text),
            SyntaxKind.CurrentAddressToken => _getCurrentPC(),
            SyntaxKind.StringLiteral => null,
            SyntaxKind.MissingToken => null,
            _ => null,
        };
    }

    private long? EvaluateName(GreenNodeBase node)
    {
        var token = (GreenToken)((GreenNode)node).GetChild(0)!;
        return EvaluateRawIdentifier(token.Text);
    }

    private long? EvaluateRawIdentifier(string name)
    {
        var sym = _symbols.Lookup(name);

        if (sym == null)
        {
            _symbols.DeclareForwardRef(name);
            return null;
        }

        return sym.State == SymbolState.Defined ? sym.Value : null;
    }

    private long? EvaluateBinary(GreenNodeBase node)
    {
        var green = (GreenNode)node;
        var left = TryEvaluate(green.GetChild(0)!);
        var op = (GreenToken)green.GetChild(1)!;
        var right = TryEvaluate(green.GetChild(2)!);

        if (left == null || right == null) return null;

        return op.Kind switch
        {
            SyntaxKind.PlusToken => left.Value + right.Value,
            SyntaxKind.MinusToken => left.Value - right.Value,
            SyntaxKind.StarToken => left.Value * right.Value,
            SyntaxKind.SlashToken when right.Value != 0 => left.Value / right.Value,
            SyntaxKind.PercentToken when right.Value != 0 => left.Value % right.Value,
            SyntaxKind.AmpersandToken => left.Value & right.Value,
            SyntaxKind.PipeToken => left.Value | right.Value,
            SyntaxKind.CaretToken => left.Value ^ right.Value,
            SyntaxKind.LessThanLessThanToken => left.Value << (int)right.Value,
            SyntaxKind.GreaterThanGreaterThanToken => left.Value >> (int)right.Value,
            SyntaxKind.EqualsEqualsToken => left.Value == right.Value ? 1L : 0L,
            SyntaxKind.BangEqualsToken => left.Value != right.Value ? 1L : 0L,
            SyntaxKind.LessThanToken => left.Value < right.Value ? 1L : 0L,
            SyntaxKind.GreaterThanToken => left.Value > right.Value ? 1L : 0L,
            SyntaxKind.LessThanEqualsToken => left.Value <= right.Value ? 1L : 0L,
            SyntaxKind.GreaterThanEqualsToken => left.Value >= right.Value ? 1L : 0L,
            SyntaxKind.AmpersandAmpersandToken => (left.Value != 0 && right.Value != 0) ? 1L : 0L,
            SyntaxKind.PipePipeToken => (left.Value != 0 || right.Value != 0) ? 1L : 0L,
            _ => null,
        };
    }

    private long? EvaluateUnary(GreenNodeBase node)
    {
        var green = (GreenNode)node;
        var op = (GreenToken)green.GetChild(0)!;
        var operand = TryEvaluate(green.GetChild(1)!);

        if (operand == null) return null;

        return op.Kind switch
        {
            SyntaxKind.MinusToken => -operand.Value,
            SyntaxKind.TildeToken => ~operand.Value,
            SyntaxKind.BangToken => operand.Value == 0 ? 1L : 0L,
            SyntaxKind.PlusToken => operand.Value,
            _ => null,
        };
    }

    private long? EvaluateParenthesized(GreenNodeBase node)
    {
        var green = (GreenNode)node;
        // Children: ( expr )
        return TryEvaluate(green.GetChild(1)!);
    }

    private long? EvaluateFunction(GreenNodeBase node)
    {
        var green = (GreenNode)node;
        var keyword = (GreenToken)green.GetChild(0)!;
        // Arguments start at index 2 (after keyword and open paren)
        var arg = green.ChildCount > 2 ? green.GetChild(2) : null;

        return keyword.Kind switch
        {
            SyntaxKind.HighKeyword when arg != null =>
                TryEvaluate(arg) is { } v ? (v >> 8) & 0xFF : null,
            SyntaxKind.LowKeyword when arg != null =>
                TryEvaluate(arg) is { } v ? v & 0xFF : null,
            // BANK, SIZEOF, STARTOF are linker-time — always null
            SyntaxKind.BankKeyword => null,
            SyntaxKind.SizeofKeyword => null,
            SyntaxKind.StartofKeyword => null,
            // DEF(symbol) — check if defined. Only valid when argument is a NameExpression;
            // any other expression kind (literal, binary, etc.) is not a symbol name reference.
            SyntaxKind.DefKeyword when arg?.Kind == SyntaxKind.NameExpression =>
                _symbols.Lookup(((GreenToken)((GreenNode)arg).GetChild(0)!).Text) is { State: SymbolState.Defined } ? 1L : 0L,
            SyntaxKind.StrcmpKeyword => EvaluateStrcmp(green),
            _ => null,
        };
    }

    private long? EvaluateStrcmp(GreenNode green)
    {
        // strcmp(str1, str2) — children: keyword, open_paren, arg1, comma, arg2, close_paren
        var arg1 = green.ChildCount > 2 ? green.GetChild(2) : null;
        var arg2 = green.ChildCount > 4 ? green.GetChild(4) : null;
        if (arg1 == null || arg2 == null) return null;

        var s1 = TryExtractString(arg1);
        var s2 = TryExtractString(arg2);
        if (s1 == null || s2 == null) return null;

        return string.Compare(s1, s2, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tries to extract the string value from a string literal expression node.
    /// Handles regular strings (with escape processing) and raw strings (no escapes).
    /// </summary>
    private static string? TryExtractString(GreenNodeBase node)
    {
        GreenToken? token = null;
        if (node is GreenNode gn && gn.Kind == SyntaxKind.LiteralExpression)
            token = gn.GetChild(0) as GreenToken;
        else if (node is GreenToken gt)
            token = gt;

        if (token?.Kind != SyntaxKind.StringLiteral) return null;

        var text = token.Text;
        return InterpretStringLiteral(text);
    }

    /// <summary>
    /// Interprets a string literal token text (including delimiters) into the
    /// actual string value. Handles raw strings (#"..." and #"""...""") and
    /// regular strings with escape sequences.
    /// </summary>
    internal static string InterpretStringLiteral(string text)
    {
        if (text.StartsWith("#\"\"\"") && text.EndsWith("\"\"\""))
        {
            // Raw triple-quoted string: #"""...""" — no escape processing
            return text[4..^3];
        }
        if (text.StartsWith("#\"") && text.EndsWith("\""))
        {
            // Raw string: #"..." — no escape processing
            return text[2..^1];
        }
        if (text.StartsWith("\"") && text.EndsWith("\"") && text.Length >= 2)
        {
            // Regular string — process escape sequences
            var inner = text[1..^1];
            return ProcessEscapes(inner);
        }
        return text;
    }

    private static string ProcessEscapes(string text)
    {
        if (!text.Contains('\\')) return text;
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                if (text[i] == '\r' || text[i] == '\n')
                {
                    // Line continuation in string: \<newline><leading whitespace> → removed
                    if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                        i++; // skip \r\n
                    // Skip leading whitespace on the continuation line
                    while (i + 1 < text.Length && (text[i + 1] == ' ' || text[i + 1] == '\t'))
                        i++;
                }
                else
                {
                    sb.Append(text[i] switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        _ => text[i], // unknown escape — keep the char as-is
                    });
                }
            }
            else
            {
                sb.Append(text[i]);
            }
        }
        return sb.ToString();
    }

    public static long? ParseNumber(string text)
    {
        if (text.StartsWith('$'))
            return TryParseBase(text.AsSpan(1), 16);
        if (text.StartsWith('%'))
            return TryParseBase(text.AsSpan(1), 2);
        if (text.StartsWith('&'))
            return TryParseBase(text.AsSpan(1), 8);
        // C-style prefixes: 0x, 0b, 0o
        if (text.Length >= 2 && text[0] == '0')
        {
            char prefix = text[1];
            if (prefix is 'x' or 'X')
                return TryParseBase(text.AsSpan(2), 16);
            if (prefix is 'b' or 'B')
                return TryParseBase(text.AsSpan(2), 2);
            if (prefix is 'o' or 'O')
                return TryParseBase(text.AsSpan(2), 8);
        }
        // Strip underscores for decimal parsing
        var stripped = StripUnderscores(text);
        if (long.TryParse(stripped, out var val))
            return val;
        return null;
    }

    private static string StripUnderscores(string text)
    {
        if (!text.Contains('_')) return text;
        return text.Replace("_", "");
    }

    private static long? TryParseBase(ReadOnlySpan<char> digits, int radix)
    {
        if (digits.IsEmpty) return null;
        long result = 0;
        foreach (char c in digits)
        {
            if (c == '_') continue; // skip underscore separators
            int d = c >= '0' && c <= '9' ? c - '0'
                  : c >= 'a' && c <= 'f' ? c - 'a' + 10
                  : c >= 'A' && c <= 'F' ? c - 'A' + 10
                  : -1;
            if (d < 0 || d >= radix) return null;
            result = result * radix + d;
        }
        return result;
    }
}
