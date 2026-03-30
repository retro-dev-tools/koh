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

    public ExpressionEvaluator(SymbolTable symbols, DiagnosticBag diagnostics,
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

            // STRLEN("str") — return character count of string literal
            case SyntaxKind.StrlenKeyword:
            {
                var s = ExtractStringArg(green, 2);
                return s?.Length;
            }

            // STRFIND("haystack", "needle") — 0-based index of first occurrence, or -1
            case SyntaxKind.StrfindKeyword:
            {
                var haystack = ExtractStringArg(green, 2);
                var needle = ExtractStringArg(green, 4);
                if (haystack == null || needle == null) return null;
                if (needle.Length == 0) return 0L;
                int idx = haystack.IndexOf(needle, StringComparison.Ordinal);
                return idx < 0 ? -1L : (long)idx;
            }

            // STRRFIND("haystack", "needle") — 0-based index of last occurrence, or -1
            case SyntaxKind.StrrfindKeyword:
            {
                var haystack = ExtractStringArg(green, 2);
                var needle = ExtractStringArg(green, 4);
                if (haystack == null || needle == null) return null;
                if (needle.Length == 0) return (long)haystack.Length;
                int idx = haystack.LastIndexOf(needle, StringComparison.Ordinal);
                return idx < 0 ? -1L : (long)idx;
            }

            // BYTELEN("str") — byte length after charmap encoding
            case SyntaxKind.BytelenKeyword:
            {
                var s = ExtractStringArg(green, 2);
                if (s == null) return null;
                if (_charMaps != null)
                    return _charMaps.EncodeString(s).Length;
                return s.Length; // fallback: ASCII byte length
            }

            // STRBYTE("str", index) — byte at 0-based index after charmap encoding; negative = from end
            case SyntaxKind.StrbyteKeyword:
            {
                var s = ExtractStringArg(green, 2);
                var idxVal = green.ChildCount > 4 ? TryEvaluate(green.GetChild(4)!) : null;
                if (s == null || !idxVal.HasValue) return null;
                var encoded = _charMaps != null ? _charMaps.EncodeString(s) : System.Text.Encoding.ASCII.GetBytes(s);
                int idx = (int)idxVal.Value;
                if (idx < 0) idx += encoded.Length; // negative index from end
                if (idx < 0 || idx >= encoded.Length)
                {
                    _diagnostics.Report(default, $"STRBYTE index {idxVal.Value} out of range for string of byte length {encoded.Length}",
                        Diagnostics.DiagnosticSeverity.Warning);
                    return 0L;
                }
                return (long)encoded[idx];
            }

            // CHARLEN("str") — character count using charmap-aware tokenization
            case SyntaxKind.CharlenKeyword:
            {
                var s = ExtractStringArg(green, 2);
                if (s == null) return null;
                if (_charMaps != null)
                    return _charMaps.CountChars(s);
                return s.Length; // fallback: plain character count
            }

            // INCHARMAP("str") — 1 if the string has a charmap mapping, 0 otherwise
            case SyntaxKind.IncharmapKeyword:
            {
                var s = ExtractStringArg(green, 2);
                if (s == null) return null;
                if (_charMaps != null)
                    return _charMaps.HasMapping(s) ? 1L : 0L;
                return 0L;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Extract a string literal value from a function call argument at the given child index.
    /// Returns the unquoted string content, or null if the argument is not a string literal.
    /// Applies string interpolation if a resolver is available.
    /// </summary>
    private string? ExtractStringArg(GreenNode funcCall, int childIndex)
    {
        if (childIndex >= funcCall.ChildCount) return null;
        var arg = funcCall.GetChild(childIndex);
        if (arg == null) return null;

        string? raw = null;

        // Direct string literal token
        if (arg is GreenToken { Kind: SyntaxKind.StringLiteral } directToken)
            raw = StripQuotes(directToken.Text);

        // LiteralExpression wrapping a StringLiteral
        if (raw == null && arg is GreenNode { Kind: SyntaxKind.LiteralExpression } litNode)
        {
            var inner = litNode.GetChild(0);
            if (inner is GreenToken { Kind: SyntaxKind.StringLiteral } strToken)
                raw = StripQuotes(strToken.Text);
        }

        if (raw != null && _resolveInterpolations != null)
            raw = _resolveInterpolations(raw);

        return raw;
    }

    private static string StripQuotes(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

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
