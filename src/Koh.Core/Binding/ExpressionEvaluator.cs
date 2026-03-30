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

    public ExpressionEvaluator(SymbolTable symbols, DiagnosticBag diagnostics,
        Func<int> getCurrentPC)
        : this(symbols, diagnostics, getCurrentPC, null)
    {
    }

    internal ExpressionEvaluator(SymbolTable symbols, DiagnosticBag diagnostics,
        Func<int> getCurrentPC, CharMapManager? charMaps)
    {
        _symbols = symbols;
        _diagnostics = diagnostics;
        _getCurrentPC = getCurrentPC;
        _charMaps = charMaps;
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
            SyntaxKind.CharLiteralToken => EvaluateCharLiteral(((GreenToken)node).Text),
            SyntaxKind.StringLiteral => null,
            _ => null,
        };
    }

    /// <summary>
    /// Evaluate an expression that may produce a string value (for ++ concatenation,
    /// === / !== comparison). Returns null for non-string expressions.
    /// </summary>
    public string? TryEvaluateString(GreenNodeBase node)
    {
        if (node.Kind == SyntaxKind.LiteralExpression)
        {
            var token = (GreenToken)((GreenNode)node).GetChild(0)!;
            if (token.Kind == SyntaxKind.StringLiteral)
                return StripQuotes(token.Text);
        }
        if (node.Kind == SyntaxKind.StringLiteral && node is GreenToken strTok)
            return StripQuotes(strTok.Text);
        if (node.Kind == SyntaxKind.BinaryExpression)
        {
            var green = (GreenNode)node;
            var op = (GreenToken)green.GetChild(1)!;
            if (op.Kind == SyntaxKind.PlusPlusToken)
            {
                var leftStr = TryEvaluateString(green.GetChild(0)!);
                var rightStr = TryEvaluateString(green.GetChild(2)!);
                if (leftStr != null && rightStr != null)
                    return leftStr + rightStr;
            }
        }
        return null;
    }

    private static string StripQuotes(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    private long EvaluateCharLiteral(string text)
    {
        // 'X' — extract the character between quotes and return its charmap/ASCII value
        if (text.Length >= 3 && text[0] == '\'' && text[^1] == '\'')
        {
            var ch = text[1..^1]; // the character(s) between quotes
            if (_charMaps != null)
            {
                var bytes = _charMaps.EncodeString(ch);
                if (bytes.Length > 0)
                {
                    // Combine bytes into a single value (big-endian)
                    long val = 0;
                    foreach (var b in bytes)
                        val = (val << 8) | b;
                    return val;
                }
            }
            return ch.Length > 0 ? ch[0] : 0;
        }
        return 0;
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
        var op = (GreenToken)green.GetChild(1)!;

        // String operators: ===, !==, ++ (++ returns string, handled via TryEvaluateString)
        if (op.Kind is SyntaxKind.EqualsEqualsEqualsToken or SyntaxKind.BangEqualsEqualsToken)
        {
            var leftStr = TryEvaluateString(green.GetChild(0)!);
            var rightStr = TryEvaluateString(green.GetChild(2)!);
            if (leftStr == null || rightStr == null) return null;
            return op.Kind == SyntaxKind.EqualsEqualsEqualsToken
                ? (leftStr == rightStr ? 1L : 0L)
                : (leftStr != rightStr ? 1L : 0L);
        }

        var left = TryEvaluate(green.GetChild(0)!);
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
            _ => null,
        };
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
