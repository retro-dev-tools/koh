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
    private readonly CharMapManager? _charMaps;
    private readonly Func<string, string>? _resolveInterpolations;

    internal ExpressionEvaluator(SymbolTable symbols, DiagnosticBag diagnostics,
        Func<int> getCurrentPC, CharMapManager? charMaps = null,
        Func<string, string>? resolveInterpolations = null)
    {
        _symbols = symbols;
        _diagnostics = diagnostics;
        _getCurrentPC = getCurrentPC;
        _charMaps = charMaps;
        _resolveInterpolations = resolveInterpolations;
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
            SyntaxKind.CharLiteralToken => EvaluateCharLiteral(((GreenToken)node).Text),
            _ => null,
        };
    }

    /// <summary>
    /// Evaluate a node as a string expression. Handles string literals and string-returning
    /// functions (STRCAT, STRSUB).
    /// </summary>
    public string? TryEvaluateString(GreenNodeBase node)
    {
        if (node.Kind == SyntaxKind.LiteralExpression)
        {
            var token = (GreenToken)((GreenNode)node).GetChild(0)!;
            if (token.Kind == SyntaxKind.StringLiteral)
            {
                var raw = token.Text.Length >= 2 ? token.Text[1..^1] : token.Text;
                var unescaped = UnescapeString(raw);
                return _resolveInterpolations != null ? _resolveInterpolations(unescaped) : unescaped;
            }
        }

        if (node.Kind == SyntaxKind.FunctionCallExpression)
        {
            var green = (GreenNode)node;
            var keyword = (GreenToken)green.GetChild(0)!;

            if (keyword.Kind == SyntaxKind.StrcatKeyword)
            {
                // STRCAT(str1, str2, ...) — concatenate strings
                var sb = new System.Text.StringBuilder();
                for (int i = 2; i < green.ChildCount; i++)
                {
                    var child = green.GetChild(i)!;
                    if (child.Kind is SyntaxKind.CommaToken or SyntaxKind.CloseParenToken)
                        continue;
                    var s = TryEvaluateString(child);
                    if (s != null) sb.Append(s);
                }
                return sb.ToString();
            }

            if (keyword.Kind == SyntaxKind.StrsubKeyword)
            {
                // STRSUB(str, pos, len) — substring (1-based position)
                var args = CollectFunctionArgs(green);
                if (args.Count >= 2)
                {
                    var str = TryEvaluateString(args[0]);
                    var pos = TryEvaluate(args[1]);
                    if (str != null && pos.HasValue)
                    {
                        int start = (int)pos.Value - 1; // 1-based to 0-based
                        int len = args.Count >= 3 && TryEvaluate(args[2]) is { } l ? (int)l : str.Length - start;
                        if (start >= 0 && start + len <= str.Length)
                            return str.Substring(start, len);
                    }
                }
                return null;
            }
        }

        // For raw StringLiteral tokens
        if (node is GreenToken tok && tok.Kind == SyntaxKind.StringLiteral)
            return UnescapeString(tok.Text.Length >= 2 ? tok.Text[1..^1] : tok.Text);

        return null;
    }

    private long? EvaluateLiteral(GreenNodeBase node)
    {
        var token = (GreenToken)((GreenNode)node).GetChild(0)!;
        return token.Kind switch
        {
            SyntaxKind.NumberLiteral => ParseNumber(token.Text),
            SyntaxKind.CurrentAddressToken => _getCurrentPC(),
            SyntaxKind.CharLiteralToken => EvaluateCharLiteral(token.Text),
            SyntaxKind.StringLiteral => null,
            SyntaxKind.MissingToken => null,
            _ => null,
        };
    }

    /// <summary>
    /// Evaluate a character literal like 'A' or '\n'. If a charmap mapping exists, returns
    /// the charmap value; otherwise returns the ASCII/Unicode code point.
    /// </summary>
    private long? EvaluateCharLiteral(string text)
    {
        if (text.Length < 2 || text[0] != '\'' || text[^1] != '\'')
            return null;

        var inner = text[1..^1];
        char ch;
        if (inner.Length == 1)
            ch = inner[0];
        else if (inner.Length == 2 && inner[0] == '\\')
        {
            ch = inner[1] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '0' => '\0',
                '\\' => '\\',
                '\'' => '\'',
                _ => inner[1],
            };
        }
        else
            return null;

        // Check charmap first
        if (_charMaps != null)
        {
            var mapped = _charMaps.LookupCharValue(ch.ToString());
            if (mapped.HasValue)
                return mapped.Value;
        }

        return ch;
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

        switch (keyword.Kind)
        {
            case SyntaxKind.HighKeyword when arg != null:
                return TryEvaluate(arg) is { } hv ? (hv >> 8) & 0xFF : null;
            case SyntaxKind.LowKeyword when arg != null:
                return TryEvaluate(arg) is { } lv ? lv & 0xFF : null;
            // BANK, SIZEOF, STARTOF are linker-time — always null
            case SyntaxKind.BankKeyword:
            case SyntaxKind.SizeofKeyword:
            case SyntaxKind.StartofKeyword:
                return null;
            // DEF(symbol) — check if defined
            case SyntaxKind.DefKeyword when arg?.Kind == SyntaxKind.NameExpression:
                return _symbols.Lookup(((GreenToken)((GreenNode)arg).GetChild(0)!).Text) is { State: SymbolState.Defined } ? 1L : 0L;

            case SyntaxKind.StrlenKeyword:
            {
                var str = arg != null ? TryEvaluateString(arg) : null;
                if (str != null) return str.Length;
                return null;
            }

            case SyntaxKind.StrcmpKeyword:
            {
                var args = CollectFunctionArgs(green);
                if (args.Count >= 2)
                {
                    var s1 = TryEvaluateString(args[0]);
                    var s2 = TryEvaluateString(args[1]);
                    if (s1 != null && s2 != null)
                        return string.Compare(s1, s2, StringComparison.Ordinal);
                }
                return null;
            }

            case SyntaxKind.CharlenKeyword:
            {
                if (_charMaps != null && arg != null)
                {
                    var str = TryEvaluateString(arg);
                    if (str != null) return _charMaps.CharLen(str);
                }
                return null;
            }

            case SyntaxKind.IncharmapKeyword:
            {
                if (_charMaps != null && arg != null)
                {
                    var str = TryEvaluateString(arg);
                    if (str != null) return _charMaps.ContainsKey(str) ? 1L : 0L;
                }
                return null;
            }

            // STRCAT returns a string, not a number — if used in numeric context, return null
            case SyntaxKind.StrcatKeyword:
            case SyntaxKind.StrsubKeyword:
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Collect function call arguments (skip open/close paren and commas).
    /// </summary>
    private static List<GreenNodeBase> CollectFunctionArgs(GreenNode green)
    {
        var args = new List<GreenNodeBase>();
        for (int i = 2; i < green.ChildCount; i++)
        {
            var child = green.GetChild(i)!;
            if (child.Kind is SyntaxKind.CommaToken or SyntaxKind.CloseParenToken
                or SyntaxKind.OpenParenToken)
                continue;
            args.Add(child);
        }
        return args;
    }

    /// <summary>
    /// Process escape sequences in a string: \n, \r, \t, \0, \\, \", etc.
    /// </summary>
    internal static string UnescapeString(string text)
    {
        if (!text.Contains('\\')) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                sb.Append(text[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    _ => text[i],
                });
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
        if (long.TryParse(text, out var val))
            return val;
        return null;
    }

    private static long? TryParseBase(ReadOnlySpan<char> digits, int radix)
    {
        if (digits.IsEmpty) return null;
        long result = 0;
        foreach (char c in digits)
        {
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
