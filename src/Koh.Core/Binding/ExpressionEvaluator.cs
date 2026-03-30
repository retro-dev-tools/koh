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
    /// <summary>
    /// Fractional bits for Q16.16 fixed-point arithmetic.
    /// </summary>
    private const int FixedQ = 16;
    private const long FixedOne = 1L << FixedQ;
    private const long FixedFracMask = FixedOne - 1;
    private const long FixedHalf = 1L << (FixedQ - 1);

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

            // Fixed-point math functions (Q16.16)
            SyntaxKind.MulKeyword => EvalFixedMul(green),
            SyntaxKind.DivFuncKeyword => EvalFixedDiv(green),
            SyntaxKind.FmodKeyword => EvalFixedFmod(green),
            SyntaxKind.PowKeyword => EvalFixedPow(green),
            SyntaxKind.LogKeyword => EvalFixedLog(green),
            SyntaxKind.RoundKeyword => EvalFixedRound(arg),
            SyntaxKind.CeilKeyword => EvalFixedCeil(arg),
            SyntaxKind.FloorKeyword => EvalFixedFloor(arg),

            _ => null,
        };
    }

    /// <summary>
    /// Evaluates two fixed-point arguments from a function call node.
    /// Children layout: [0]=keyword [1]=( [2]=arg1 [3]=, [4]=arg2 [5]=)
    /// </summary>
    private (long a, long b)? EvalTwoFixedArgs(GreenNode green)
    {
        var a = TryEvaluate(green.GetChild(2)!);
        var b = green.ChildCount > 4 ? TryEvaluate(green.GetChild(4)!) : null;
        if (a is null || b is null) return null;
        return (a.Value, b.Value);
    }

    private long? EvalFixedMul(GreenNode green) =>
        EvalTwoFixedArgs(green) is var (a, b) ? (a * b) >> FixedQ : null;

    private long? EvalFixedDiv(GreenNode green)
    {
        if (EvalTwoFixedArgs(green) is not var (a, b)) return null;
        if (b == 0)
            return a == 0 ? 0 : a > 0 ? 0x7FFFFFFF : unchecked((long)(uint)0x80000000);
        return (a << FixedQ) / b;
    }

    private long? EvalFixedFmod(GreenNode green) =>
        EvalTwoFixedArgs(green) is var (a, b) ? b == 0 ? 0 : a % b : null;

    private long? EvalFixedPow(GreenNode green)
    {
        if (EvalTwoFixedArgs(green) is not var (a, b)) return null;
        double result = Math.Pow(a / (double)FixedOne, b / (double)FixedOne);
        return (long)Math.Round(result * FixedOne);
    }

    private long? EvalFixedLog(GreenNode green)
    {
        if (EvalTwoFixedArgs(green) is not var (a, b)) return null;
        double result = Math.Log(a / (double)FixedOne, b / (double)FixedOne);
        return (long)Math.Round(result * FixedOne);
    }

    private long? EvalFixedUnary(GreenNodeBase? arg, Func<long, long> op)
    {
        if (arg is null) return null;
        var v = TryEvaluate(arg);
        return v is null ? null : op(v.Value);
    }

    private long? EvalFixedRound(GreenNodeBase? arg) =>
        EvalFixedUnary(arg, val => val >= 0
            ? (val + FixedHalf) & ~FixedFracMask
            : -(((-val) + FixedHalf) & ~FixedFracMask));

    private long? EvalFixedCeil(GreenNodeBase? arg) =>
        EvalFixedUnary(arg, val => val >= 0
            ? (val & FixedFracMask) != 0 ? (val & ~FixedFracMask) + FixedOne : val
            : -((-val) & ~FixedFracMask));

    private long? EvalFixedFloor(GreenNodeBase? arg) =>
        EvalFixedUnary(arg, val => val >= 0
            ? val & ~FixedFracMask
            : ((-val) & FixedFracMask) != 0 ? -(((-val) & ~FixedFracMask) + FixedOne) : val);

    public static long? ParseNumber(string text)
    {
        var clean = text.Contains('_') ? text.Replace("_", "") : text;

        if (clean.StartsWith('$'))
            return TryParseBase(clean.AsSpan(1), 16);
        if (clean.StartsWith('%'))
            return TryParseBase(clean.AsSpan(1), 2);
        if (clean.StartsWith('&'))
            return TryParseBase(clean.AsSpan(1), 8);

        int dot = clean.IndexOf('.');
        if (dot >= 0)
            return ParseFixedPoint(clean, dot);

        if (long.TryParse(clean, out var val))
            return val;
        return null;
    }

    private static long? ParseFixedPoint(string text, int dot)
    {
        var intPart = text.AsSpan(0, dot);
        var fracPart = text.AsSpan(dot + 1);

        if (!long.TryParse(intPart, out long integer))
            return null;

        long result = integer << FixedQ;
        if (fracPart.Length > 0)
        {
            if (!long.TryParse(fracPart, out long fracVal))
                return null;
            double frac = fracVal / Math.Pow(10, fracPart.Length);
            long fracFixed = (long)Math.Round(frac * FixedOne);
            result += integer >= 0 ? fracFixed : -fracFixed;
        }
        return result;
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
