using System.Numerics;
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
    private readonly int _fracBits;

    public ExpressionEvaluator(SymbolTable symbols, DiagnosticBag diagnostics,
        Func<int> getCurrentPC, int fracBits = 0)
    {
        _symbols = symbols;
        _diagnostics = diagnostics;
        _getCurrentPC = getCurrentPC;
        _fracBits = fracBits;
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
            SyntaxKind.NumberLiteral => ParseNumberWithFixedPoint(((GreenToken)node).Text),
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
            SyntaxKind.NumberLiteral => ParseNumberWithFixedPoint(token.Text),
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
        // Second argument at index 4 (after comma at index 3)
        var arg2 = green.ChildCount > 4 ? green.GetChild(4) : null;

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

            // Trigonometry — fixed-point where 1.0 = full turn
            SyntaxKind.SinKeyword when arg != null => EvaluateTrigFunction(arg, Math.Sin),
            SyntaxKind.CosKeyword when arg != null => EvaluateTrigFunction(arg, Math.Cos),
            SyntaxKind.TanKeyword when arg != null => EvaluateTrigFunction(arg, Math.Tan),
            SyntaxKind.AsinKeyword when arg != null => EvaluateInverseTrigFunction(arg, Math.Asin),
            SyntaxKind.AcosKeyword when arg != null => EvaluateInverseTrigFunction(arg, Math.Acos),
            SyntaxKind.AtanKeyword when arg != null => EvaluateInverseTrigFunction(arg, Math.Atan),
            SyntaxKind.Atan2Keyword when arg != null && arg2 != null => EvaluateAtan2(arg, arg2),
            // Bit functions
            SyntaxKind.BitwidthKeyword when arg != null =>
                TryEvaluate(arg) is { } bw ? EvaluateBitwidth(bw) : null,
            SyntaxKind.TzcountKeyword when arg != null =>
                TryEvaluate(arg) is { } tz ? EvaluateTzcount(tz) : null,
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

    private double FixedToDouble(long v) => v / (double)(1L << _fracBits);
    private long DoubleToFixed(double d) => (long)Math.Round(d * (1L << _fracBits));

    /// <summary>Forward trig: fixed-point turns in, fixed-point result out.</summary>
    private long? EvaluateTrigFunction(GreenNodeBase arg, Func<double, double> trigFn)
    {
        if (TryEvaluate(arg) is not { } v || _fracBits == 0) return null;
        double angleRad = FixedToDouble(v) * 2.0 * Math.PI;
        return DoubleToFixed(trigFn(angleRad));
    }

    /// <summary>Inverse trig: fixed-point value in, fixed-point turns out.</summary>
    private long? EvaluateInverseTrigFunction(GreenNodeBase arg, Func<double, double> invTrigFn)
    {
        if (TryEvaluate(arg) is not { } v || _fracBits == 0) return null;
        double turns = invTrigFn(FixedToDouble(v)) / (2.0 * Math.PI);
        return DoubleToFixed(turns);
    }

    private long? EvaluateAtan2(GreenNodeBase arg1, GreenNodeBase arg2)
    {
        if (TryEvaluate(arg1) is not { } y || TryEvaluate(arg2) is not { } x || _fracBits == 0)
            return null;
        double turns = Math.Atan2(FixedToDouble(y), FixedToDouble(x)) / (2.0 * Math.PI);
        return DoubleToFixed(turns);
    }

    private static long? EvaluateBitwidth(long v)
    {
        if (v == 0) return 0;
        return 32 - BitOperations.LeadingZeroCount((uint)(v & 0xFFFFFFFF));
    }

    private static long? EvaluateTzcount(long v)
    {
        uint uv = (uint)(v & 0xFFFFFFFF);
        return uv == 0 ? 32 : BitOperations.TrailingZeroCount(uv);
    }

    public long? ParseNumberWithFixedPoint(string text) => ParseNumber(text, _fracBits);

    public static long? ParseNumber(string text) => ParseNumber(text, 0);

    public static long? ParseNumber(string text, int fracBits)
    {
        var clean = text.Contains('_') ? text.Replace("_", "") : text;

        if (clean.StartsWith('$'))
            return TryParseBase(clean.AsSpan(1), 16);
        if (clean.StartsWith('%'))
            return TryParseBase(clean.AsSpan(1), 2);
        if (clean.StartsWith('&'))
            return TryParseBase(clean.AsSpan(1), 8);
        // Fixed-point literal: digits.digits
        if (fracBits > 0 && clean.Contains('.'))
        {
            if (double.TryParse(clean, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
                return (long)Math.Round(d * (1L << fracBits));
            return null;
        }

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
