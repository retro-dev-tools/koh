using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Binding;

/// <summary>
/// Folds constant expressions. Returns null for forward references or
/// linker-time symbols (BANK, SIZEOF, STARTOF).
///
/// Fixed-point arithmetic uses Q16.16 format by default (configurable via OPT Q.N).
/// All fixed-point values are stored as 32-bit integers where the lower N bits are
/// the fractional part. Fixed-point literals (e.g. 1.5) are converted at parse time.
/// </summary>
public sealed class ExpressionEvaluator
{
    private readonly SymbolTable _symbols;
    private readonly DiagnosticBag _diagnostics;
    private readonly Func<int> _getCurrentPC;
    internal CharMapManager? CharMaps { get; set; }
    internal ISourceFileResolver? FileResolver { get; set; }
    private int _fixedPointBits = 16; // Q.16 default

    public int FixedPointBits
    {
        get => _fixedPointBits;
        set => _fixedPointBits = Math.Clamp(value, 1, 31);
    }

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

    /// <summary>
    /// Try to evaluate a node as a string value (for string operations).
    /// </summary>
    public string? TryEvaluateString(GreenNodeBase node)
    {
        if (node is GreenToken tok && tok.Kind == SyntaxKind.StringLiteral)
            return StripQuotes(tok.Text);
        if (node.Kind == SyntaxKind.LiteralExpression)
        {
            var child = ((GreenNode)node).GetChild(0);
            if (child is GreenToken t && t.Kind == SyntaxKind.StringLiteral)
                return StripQuotes(t.Text);
        }
        if (node.Kind == SyntaxKind.BinaryExpression)
        {
            var green = (GreenNode)node;
            var op = (GreenToken)green.GetChild(1)!;
            if (op.Kind == SyntaxKind.PlusPlusToken)
            {
                var left = TryEvaluateString(green.GetChild(0)!);
                var right = TryEvaluateString(green.GetChild(2)!);
                if (left != null && right != null)
                    return left + right;
            }
            if (op.Kind == SyntaxKind.EqualsEqualsEqualsToken)
            {
                var left = TryEvaluateString(green.GetChild(0)!);
                var right = TryEvaluateString(green.GetChild(2)!);
                // Return null - numeric evaluation handles this
            }
        }
        // Function calls that return strings
        if (node.Kind == SyntaxKind.FunctionCallExpression)
        {
            var green = (GreenNode)node;
            var keyword = (GreenToken)green.GetChild(0)!;
            return EvaluateStringFunction(green, keyword);
        }
        return null;
    }

    private static string StripQuotes(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

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
        var op = (GreenToken)green.GetChild(1)!;

        // String operators: ===, !==, ++
        if (op.Kind is SyntaxKind.EqualsEqualsEqualsToken or SyntaxKind.BangEqualsEqualsToken
            or SyntaxKind.PlusPlusToken)
        {
            var leftStr = TryEvaluateString(green.GetChild(0)!);
            var rightStr = TryEvaluateString(green.GetChild(2)!);

            if (op.Kind == SyntaxKind.EqualsEqualsEqualsToken)
            {
                if (leftStr != null && rightStr != null)
                    return leftStr == rightStr ? 1L : 0L;
                return null;
            }
            if (op.Kind == SyntaxKind.BangEqualsEqualsToken)
            {
                if (leftStr != null && rightStr != null)
                    return leftStr != rightStr ? 1L : 0L;
                return null;
            }
            // ++ is string concat — result is a string, not numeric
            return null;
        }

        var left = TryEvaluate(green.GetChild(0)!);
        var right = TryEvaluate(green.GetChild(2)!);

        if (left == null || right == null) return null;

        return op.Kind switch
        {
            SyntaxKind.PlusToken => left.Value + right.Value,
            SyntaxKind.MinusToken => left.Value - right.Value,
            SyntaxKind.StarToken => left.Value * right.Value,
            SyntaxKind.SlashToken when right.Value != 0 => TruncateDiv(left.Value, right.Value),
            SyntaxKind.SlashToken => ReportDivByZero(),
            SyntaxKind.PercentToken when right.Value != 0 => TruncateMod(left.Value, right.Value),
            SyntaxKind.PercentToken => ReportDivByZero(),
            SyntaxKind.AmpersandToken => left.Value & right.Value,
            SyntaxKind.PipeToken => left.Value | right.Value,
            SyntaxKind.CaretToken => left.Value ^ right.Value,
            SyntaxKind.LessThanLessThanToken => left.Value << (int)right.Value,
            SyntaxKind.GreaterThanGreaterThanToken => ArithmeticRightShift(left.Value, (int)right.Value),
            SyntaxKind.EqualsEqualsToken => left.Value == right.Value ? 1L : 0L,
            SyntaxKind.BangEqualsToken => left.Value != right.Value ? 1L : 0L,
            SyntaxKind.LessThanToken => left.Value < right.Value ? 1L : 0L,
            SyntaxKind.GreaterThanToken => left.Value > right.Value ? 1L : 0L,
            SyntaxKind.LessThanEqualsToken => left.Value <= right.Value ? 1L : 0L,
            SyntaxKind.GreaterThanEqualsToken => left.Value >= right.Value ? 1L : 0L,
            SyntaxKind.AmpersandAmpersandToken => (left.Value != 0 && right.Value != 0) ? 1L : 0L,
            SyntaxKind.PipePipeToken => (left.Value != 0 || right.Value != 0) ? 1L : 0L,
            SyntaxKind.StarStarToken => IntPow(left.Value, right.Value),
            _ => null,
        };
    }

    private long? ReportDivByZero()
    {
        _diagnostics.Report(default, "Division by zero");
        return null;
    }

    /// <summary>
    /// RGBDS-compatible truncating integer division (toward zero) using 32-bit signed arithmetic.
    /// </summary>
    private static long TruncateDiv(long a, long b)
    {
        int ai = (int)a, bi = (int)b;
        // INT32_MIN / -1 overflows in 32-bit — return INT32_MIN (RGBDS behavior)
        if (ai == int.MinValue && bi == -1) return int.MinValue;
        return ai / bi;
    }

    /// <summary>
    /// RGBDS-compatible truncating modulo (sign follows dividend) using 32-bit signed arithmetic.
    /// </summary>
    private static long TruncateMod(long a, long b)
    {
        int ai = (int)a, bi = (int)b;
        if (ai == int.MinValue && bi == -1) return 0;
        return ai % bi;
    }

    /// <summary>
    /// Arithmetic right shift — sign-extends for negative values.
    /// RGBDS uses 32-bit arithmetic, so we cast to int first.
    /// </summary>
    private static long ArithmeticRightShift(long val, int shift)
    {
        int v32 = (int)val;
        return v32 >> shift;
    }

    /// <summary>
    /// Integer exponentiation.
    /// </summary>
    private static long IntPow(long baseVal, long exp)
    {
        if (exp < 0) return 0;
        long result = 1;
        long b = baseVal;
        long e = exp;
        while (e > 0)
        {
            if ((e & 1) != 0)
                result *= b;
            b *= b;
            e >>= 1;
        }
        return result;
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

    /// <summary>
    /// Get the Nth argument from a FunctionCallExpression green node.
    /// Arguments are at indices 2, 4, 6, ... (interleaved with commas at 3, 5, ...).
    /// </summary>
    private static GreenNodeBase? GetFuncArg(GreenNode green, int n)
    {
        int idx = 2 + n * 2;
        return idx < green.ChildCount ? green.GetChild(idx) : null;
    }

    /// <summary>Evaluate a two-argument fixed-point function.</summary>
    private long? EvalFixedBinary(GreenNode green, Func<long, long, long> op)
    {
        var a = GetFuncArg(green, 0) is { } n0 ? TryEvaluate(n0) : null;
        var b = GetFuncArg(green, 1) is { } n1 ? TryEvaluate(n1) : null;
        return a.HasValue && b.HasValue ? op(a.Value, b.Value) : null;
    }

    /// <summary>Evaluate a single-argument function returning long.</summary>
    private long? EvalUnaryFunc(GreenNodeBase? arg, Func<long, long> op)
    {
        if (arg == null) return null;
        var v = TryEvaluate(arg);
        return v.HasValue ? op(v.Value) : null;
    }

    private long? EvaluateFunction(GreenNodeBase node)
    {
        var green = (GreenNode)node;
        var keyword = (GreenToken)green.GetChild(0)!;
        var arg = GetFuncArg(green, 0);

        switch (keyword.Kind)
        {
            case SyntaxKind.HighKeyword when arg != null:
                return TryEvaluate(arg) is { } hv ? (hv >> 8) & 0xFF : null;
            case SyntaxKind.LowKeyword when arg != null:
                return TryEvaluate(arg) is { } lv ? lv & 0xFF : null;

            // BANK, SIZEOF, STARTOF are linker-time — always null
            case SyntaxKind.BankKeyword: return null;
            case SyntaxKind.SizeofKeyword: return null;
            case SyntaxKind.StartofKeyword: return null;

            // DEF(symbol) — check if defined
            case SyntaxKind.DefKeyword when arg?.Kind == SyntaxKind.NameExpression:
                return _symbols.Lookup(((GreenToken)((GreenNode)arg).GetChild(0)!).Text) is { State: SymbolState.Defined } ? 1L : 0L;

            // ISCONST — always return 1 for constants known at assembly time
            case SyntaxKind.IsConstKeyword when arg != null:
                return TryEvaluate(arg) != null ? 1L : 0L;

            // String functions that return integers
            case SyntaxKind.StrlenKeyword when arg != null:
            {
                var s = TryEvaluateString(arg);
                return s != null ? s.Length : null;
            }

            case SyntaxKind.StrcmpKeyword:
            {
                var s1 = arg != null ? TryEvaluateString(arg) : null;
                var a2 = GetFuncArg(green, 1);
                var s2 = a2 != null ? TryEvaluateString(a2) : null;
                if (s1 != null && s2 != null)
                    return string.Compare(s1, s2, StringComparison.Ordinal);
                return null;
            }

            case SyntaxKind.StrfindKeyword:
            {
                var haystack = arg != null ? TryEvaluateString(arg) : null;
                var a2 = GetFuncArg(green, 1);
                var needle = a2 != null ? TryEvaluateString(a2) : null;
                if (haystack != null && needle != null)
                {
                    if (needle.Length == 0) return 0;
                    return haystack.IndexOf(needle, StringComparison.Ordinal);
                }
                return null;
            }

            case SyntaxKind.StrrfindKeyword:
            {
                var haystack = arg != null ? TryEvaluateString(arg) : null;
                var a2 = GetFuncArg(green, 1);
                var needle = a2 != null ? TryEvaluateString(a2) : null;
                if (haystack != null && needle != null)
                {
                    if (needle.Length == 0) return haystack.Length;
                    return haystack.LastIndexOf(needle, StringComparison.Ordinal);
                }
                return null;
            }

            case SyntaxKind.BytelenKeyword when arg != null:
            {
                var s = TryEvaluateString(arg);
                return s != null ? s.Length : null;
            }

            case SyntaxKind.StrbyteKeyword:
            {
                var s = arg != null ? TryEvaluateString(arg) : null;
                var a2 = GetFuncArg(green, 1);
                var idx = a2 != null ? TryEvaluate(a2) : null;
                if (s != null && idx.HasValue)
                {
                    int i = (int)idx.Value;
                    if (i < 0) i = s.Length + i; // negative index from end
                    if (i >= 0 && i < s.Length)
                        return (byte)s[i];
                    return 0; // out of bounds
                }
                return null;
            }

            case SyntaxKind.CharlenKeyword when arg != null:
            {
                var s = TryEvaluateString(arg);
                if (s != null && CharMaps != null)
                    return CharMaps.CharLen(s);
                return s?.Length;
            }

            case SyntaxKind.IncharmapKeyword when arg != null:
            {
                var s = TryEvaluateString(arg);
                if (s != null && CharMaps != null)
                    return CharMaps.InCharMap(s) ? 1L : 0L;
                return null;
            }

            // SECTION(label) — returns the section name as string, not a number
            case SyntaxKind.SectionFuncKeyword:
            {
                // This returns a string — cannot be a number. Error for non-label args.
                if (arg?.Kind == SyntaxKind.NameExpression)
                {
                    var name = ((GreenToken)((GreenNode)arg).GetChild(0)!).Text;
                    var sym = _symbols.Lookup(name);
                    if (sym?.Kind == SymbolKind.Label && sym.Section != null)
                        return null; // SECTION() returns string, not numeric
                    _diagnostics.Report(default, $"SECTION() argument '{name}' is not a label");
                    return null;
                }
                _diagnostics.Report(default, "SECTION() requires a label argument");
                return null;
            }

            // Fixed-point math functions (two-argument)
            case SyntaxKind.MulKeyword: return EvalFixedBinary(green, FixedMul);
            case SyntaxKind.DivFuncKeyword: return EvalFixedBinary(green, FixedDiv);
            case SyntaxKind.FmodKeyword: return EvalFixedBinary(green, FixedFmod);
            case SyntaxKind.PowKeyword: return EvalFixedBinary(green, FixedPow);
            case SyntaxKind.LogKeyword: return EvalFixedBinary(green, FixedLog);
            case SyntaxKind.Atan2Keyword: return EvalFixedBinary(green, FixedAtan2);

            // Fixed-point math functions (single-argument)
            case SyntaxKind.RoundKeyword: return EvalUnaryFunc(arg, FixedRound);
            case SyntaxKind.CeilKeyword: return EvalUnaryFunc(arg, FixedCeil);
            case SyntaxKind.FloorKeyword: return EvalUnaryFunc(arg, FixedFloor);

            // Trigonometric functions (input in turns, Q format)
            case SyntaxKind.SinKeyword: return EvalUnaryFunc(arg, FixedSin);
            case SyntaxKind.CosKeyword: return EvalUnaryFunc(arg, FixedCos);
            case SyntaxKind.TanKeyword: return EvalUnaryFunc(arg, FixedTan);
            case SyntaxKind.AsinKeyword: return EvalUnaryFunc(arg, FixedAsin);
            case SyntaxKind.AcosKeyword: return EvalUnaryFunc(arg, FixedAcos);
            case SyntaxKind.AtanKeyword: return EvalUnaryFunc(arg, FixedAtan);

            // Integer bit functions
            case SyntaxKind.BitwidthKeyword when arg != null:
            {
                var v = TryEvaluate(arg);
                if (v.HasValue)
                {
                    int val = (int)v.Value;
                    if (val == 0) return 0;
                    return 32 - int.LeadingZeroCount(val < 0 ? (int)((uint)val) : val);
                }
                return null;
            }
            case SyntaxKind.TzcountKeyword when arg != null:
            {
                var v = TryEvaluate(arg);
                if (v.HasValue)
                {
                    int val = (int)v.Value;
                    if (val == 0) return 32;
                    return int.TrailingZeroCount(val);
                }
                return null;
            }

            default: return null;
        }
    }

    /// <summary>
    /// Evaluate string-returning functions (STRUPR, STRLWR, STRCAT, STRSUB, READFILE, etc.)
    /// </summary>
    private string? EvaluateStringFunction(GreenNode green, GreenToken keyword)
    {
        var arg0 = GetFuncArg(green, 0);

        switch (keyword.Kind)
        {
            case SyntaxKind.StruprKeyword:
            {
                var s = arg0 != null ? TryEvaluateString(arg0) : null;
                return s?.ToUpperInvariant();
            }
            case SyntaxKind.StrlwrKeyword:
            {
                var s = arg0 != null ? TryEvaluateString(arg0) : null;
                return s?.ToLowerInvariant();
            }
            case SyntaxKind.StrcatKeyword:
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; ; i++)
                {
                    var a = GetFuncArg(green, i);
                    if (a == null) break;
                    var s = TryEvaluateString(a);
                    if (s == null) return null;
                    sb.Append(s);
                }
                return sb.ToString();
            }
            case SyntaxKind.StrsubKeyword:
            {
                var s = arg0 != null ? TryEvaluateString(arg0) : null;
                var start = GetFuncArg(green, 1) is { } a1 ? TryEvaluate(a1) : null;
                var len = GetFuncArg(green, 2) is { } a2 ? TryEvaluate(a2) : null;
                if (s == null || !start.HasValue) return null;
                int idx = (int)start.Value - 1; // RGBDS STRSUB is 1-based
                int count = len.HasValue ? (int)len.Value : s.Length - idx;
                if (idx < 0) idx = 0;
                if (idx + count > s.Length) count = s.Length - idx;
                if (count < 0) count = 0;
                return s.Substring(idx, count);
            }
            case SyntaxKind.ReadfileKeyword:
            {
                var filename = arg0 != null ? TryEvaluateString(arg0) : null;
                if (filename == null || FileResolver == null) return null;
                var resolved = FileResolver.ResolvePath("", filename);
                if (!FileResolver.FileExists(resolved)) return null;
                try
                {
                    var limitArg = GetFuncArg(green, 1);
                    var content = FileResolver.ReadAllText(resolved);
                    if (limitArg != null)
                    {
                        var limit = TryEvaluate(limitArg);
                        if (limit.HasValue && limit.Value < content.Length)
                            content = content[..(int)limit.Value];
                    }
                    return content;
                }
                catch { return null; }
            }
            default: return null;
        }
    }

    // =========================================================================
    // Fixed-point arithmetic (Q.N format)
    // =========================================================================

    private double FixedToDouble(long val)
    {
        return (double)(int)val / (1 << _fixedPointBits);
    }

    private long DoubleToFixed(double val)
    {
        return (int)Math.Round(val * (1 << _fixedPointBits));
    }

    private long FixedMul(long a, long b)
    {
        // (a * b) >> Q
        long result = ((long)(int)a * (int)b) >> _fixedPointBits;
        return (int)result;
    }

    private long FixedDiv(long a, long b)
    {
        if ((int)b == 0)
        {
            if ((int)a > 0) return 0x7FFFFFFF; // +inf
            if ((int)a < 0) return unchecked((int)0x80000000); // -inf
            return 0; // 0/0 = nan = 0
        }
        long result = ((long)(int)a << _fixedPointBits) / (int)b;
        return (int)result;
    }

    private long FixedFmod(long a, long b)
    {
        if ((int)b == 0) return 0; // nan
        double da = FixedToDouble(a);
        double db = FixedToDouble(b);
        return DoubleToFixed(da % db);
    }

    private long FixedPow(long a, long b)
    {
        double da = FixedToDouble(a);
        double db = FixedToDouble(b);
        return DoubleToFixed(Math.Pow(da, db));
    }

    private long FixedLog(long a, long b)
    {
        double da = FixedToDouble(a);
        double db = FixedToDouble(b);
        return DoubleToFixed(Math.Log(da, db));
    }

    private long FixedRound(long val)
    {
        double d = FixedToDouble(val);
        return DoubleToFixed(Math.Round(d, MidpointRounding.AwayFromZero));
    }

    private long FixedCeil(long val)
    {
        double d = FixedToDouble(val);
        return DoubleToFixed(Math.Ceiling(d));
    }

    private long FixedFloor(long val)
    {
        double d = FixedToDouble(val);
        return DoubleToFixed(Math.Floor(d));
    }

    // Trig functions: input in turns (0.0 to 1.0 = full circle)
    private long FixedSin(long val)
    {
        double turns = FixedToDouble(val);
        return DoubleToFixed(Math.Sin(turns * 2 * Math.PI));
    }

    private long FixedCos(long val)
    {
        double turns = FixedToDouble(val);
        return DoubleToFixed(Math.Cos(turns * 2 * Math.PI));
    }

    private long FixedTan(long val)
    {
        double turns = FixedToDouble(val);
        return DoubleToFixed(Math.Tan(turns * 2 * Math.PI));
    }

    private long FixedAsin(long val)
    {
        double v = FixedToDouble(val);
        return DoubleToFixed(Math.Asin(v) / (2 * Math.PI));
    }

    private long FixedAcos(long val)
    {
        double v = FixedToDouble(val);
        return DoubleToFixed(Math.Acos(v) / (2 * Math.PI));
    }

    private long FixedAtan(long val)
    {
        double v = FixedToDouble(val);
        return DoubleToFixed(Math.Atan(v) / (2 * Math.PI));
    }

    private long FixedAtan2(long y, long x)
    {
        double dy = FixedToDouble(y);
        double dx = FixedToDouble(x);
        return DoubleToFixed(Math.Atan2(dy, dx) / (2 * Math.PI));
    }

    // =========================================================================
    // Number parsing
    // =========================================================================

    public static long? ParseNumber(string text)
    {
        // Strip underscores from numeric literals
        if (text.Contains('_'))
            text = text.Replace("_", "");

        // Fixed-point literal: e.g. 1.5 → integer part * (1 << Q) + fractional part
        if (text.Contains('.') && !text.StartsWith('$') && !text.StartsWith('%') && !text.StartsWith('&'))
        {
            if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var dval))
            {
                // Default Q.16 — the caller should set the appropriate Q value
                // For parsing, we always use Q.16 as default; OPT Q.N changes it at eval time
                return (int)Math.Round(dval * (1 << 16));
            }
            return null;
        }

        if (text.StartsWith('$'))
            return TryParseBase(text.AsSpan(1), 16);
        if (text.StartsWith('%'))
            return TryParseBase(text.AsSpan(1), 2);
        if (text.StartsWith('&'))
            return TryParseBase(text.AsSpan(1), 8);

        // C-style prefixes
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return TryParseBase(text.AsSpan(2), 16);
        if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return TryParseBase(text.AsSpan(2), 2);
        if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            return TryParseBase(text.AsSpan(2), 8);

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
            if (c == '_') continue; // skip underscores
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
