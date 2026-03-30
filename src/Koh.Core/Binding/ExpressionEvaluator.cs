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
    private readonly CharMapManager? _charMaps;
    private readonly Func<string, string>? _resolveInterpolations;

    public ExpressionEvaluator(SymbolTable symbols, DiagnosticBag diagnostics,
        Func<int> getCurrentPC, int fracBits = 0)
        : this(symbols, diagnostics, getCurrentPC, fracBits, null, null)
    {
    }

    internal ExpressionEvaluator(SymbolTable symbols, DiagnosticBag diagnostics,
        Func<int> getCurrentPC, int fracBits, CharMapManager? charMaps,
        Func<string, string>? resolveInterpolations = null)
    {
        _symbols = symbols;
        _diagnostics = diagnostics;
        _getCurrentPC = getCurrentPC;
        _fracBits = fracBits;
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
            SyntaxKind.NumberLiteral => ParseNumberWithFixedPoint(((GreenToken)node).Text),
            SyntaxKind.CurrentAddressToken or SyntaxKind.AtToken => _getCurrentPC(),
            SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken =>
                EvaluateRawIdentifier(((GreenToken)node).Text),
            SyntaxKind.CharLiteralToken => EvaluateCharLiteral(((GreenToken)node).Text),
            SyntaxKind.AnonLabelForwardToken => EvaluateAnonRef(((GreenToken)node).Text, forward: true),
            SyntaxKind.AnonLabelBackwardToken => EvaluateAnonRef(((GreenToken)node).Text, forward: false),
            SyntaxKind.StringLiteral => null,
            _ => null,
        };
    }

    /// <summary>
    /// Evaluate an expression that may produce a string value (for ++ concatenation,
    /// === / !== comparison, and string-returning functions like STRCAT, STRSUB).
    /// Returns null for non-string expressions.
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

        if (node.Kind == SyntaxKind.StringLiteral && node is GreenToken strTok)
        {
            var raw = strTok.Text.Length >= 2 ? strTok.Text[1..^1] : strTok.Text;
            return UnescapeString(raw);
        }

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


    private long? EvaluateLiteral(GreenNodeBase node)
    {
        var token = (GreenToken)((GreenNode)node).GetChild(0)!;
        return token.Kind switch
        {
            SyntaxKind.NumberLiteral => ParseNumberWithFixedPoint(token.Text),
            SyntaxKind.CurrentAddressToken or SyntaxKind.AtToken => _getCurrentPC(),
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

    /// <summary>
    /// Evaluate an anonymous label reference (:+, :-, :++, etc.).
    /// The text is ":+" (1 forward), ":++" (2 forward), ":-" (1 backward), etc.
    /// </summary>
    private long? EvaluateAnonRef(string text, bool forward)
    {
        // Count the + or - characters after the colon
        int count = text.Length - 1; // subtract the colon
        int offset = forward ? count : -count;
        var sym = _symbols.ResolveAnonymousRef(offset);
        if (sym != null && sym.State == SymbolState.Defined)
            return sym.Value;
        return null;
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
            SyntaxKind.StarStarToken => IntegerPow(left.Value, right.Value),
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

            // STRCAT returns a string, not a number — if used in numeric context, return null
            case SyntaxKind.StrcatKeyword:
            case SyntaxKind.StrsubKeyword:
                return null;

            // Fixed-point math functions (Q16.16)
            case SyntaxKind.MulKeyword:
                return EvalFixedMul(green);
            case SyntaxKind.DivFuncKeyword:
                return EvalFixedDiv(green);
            case SyntaxKind.FmodKeyword:
                return EvalFixedFmod(green);
            case SyntaxKind.PowKeyword:
                return EvalFixedPow(green);
            case SyntaxKind.LogKeyword:
                return EvalFixedLog(green);
            case SyntaxKind.RoundKeyword:
                return EvalFixedRound(arg);
            case SyntaxKind.CeilKeyword:
                return EvalFixedCeil(arg);
            case SyntaxKind.FloorKeyword:
                return EvalFixedFloor(arg);

            // Trigonometry — fixed-point where 1.0 = full turn
            case SyntaxKind.SinKeyword when arg != null:
                return EvaluateTrigFunction(arg, Math.Sin);
            case SyntaxKind.CosKeyword when arg != null:
                return EvaluateTrigFunction(arg, Math.Cos);
            case SyntaxKind.TanKeyword when arg != null:
                return EvaluateTrigFunction(arg, Math.Tan);
            case SyntaxKind.AsinKeyword when arg != null:
                return EvaluateInverseTrigFunction(arg, Math.Asin);
            case SyntaxKind.AcosKeyword when arg != null:
                return EvaluateInverseTrigFunction(arg, Math.Acos);
            case SyntaxKind.AtanKeyword when arg != null:
                return EvaluateInverseTrigFunction(arg, Math.Atan);
            case SyntaxKind.Atan2Keyword when arg != null && arg2 != null:
                return EvaluateAtan2(arg, arg2);
            // Bit functions
            case SyntaxKind.BitwidthKeyword when arg != null:
                return TryEvaluate(arg) is { } bw ? EvaluateBitwidth(bw) : null;
            case SyntaxKind.TzcountKeyword when arg != null:
                return TryEvaluate(arg) is { } tz ? EvaluateTzcount(tz) : null;

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

    private static long IntegerPow(long baseVal, long exp)
    {
        if (exp < 0) return 0;
        long result = 1;
        for (long i = 0; i < exp; i++)
            result *= baseVal;
        return result;
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

    public long? ParseNumberWithFixedPoint(string text) => ParseNumber(text, _fracBits);

    public static long? ParseNumber(string text) => ParseNumber(text, 0);

    public static long? ParseNumber(string text, int fracBits)
    {
        var clean = StripUnderscores(text);

        if (clean.StartsWith('$'))
            return TryParseBase(clean.AsSpan(1), 16);
        if (clean.StartsWith('%'))
            return TryParseBase(clean.AsSpan(1), 2);
        if (clean.StartsWith('&'))
            return TryParseBase(clean.AsSpan(1), 8);
        // C-style prefixes: 0x, 0b, 0o
        if (clean.Length >= 2 && clean[0] == '0')
        {
            char prefix = clean[1];
            if (prefix is 'x' or 'X')
                return TryParseBase(clean.AsSpan(2), 16);
            if (prefix is 'b' or 'B')
                return TryParseBase(clean.AsSpan(2), 2);
            if (prefix is 'o' or 'O')
                return TryParseBase(clean.AsSpan(2), 8);
        }
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

