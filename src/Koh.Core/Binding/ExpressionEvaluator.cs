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

    private readonly SymbolTable _symbols;
    private readonly DiagnosticBag _diagnostics;
    private readonly Func<int> _getCurrentPC;
    private readonly int _fracBits;
    private readonly CharMapManager? _charMaps;
    private readonly Func<string, string>? _resolveInterpolations;

    /// <summary>Fixed-point fractional bits (Q.N). Default is 16.</summary>
    public int FracBits { get; set; } = 16;

    /// <summary>
    /// Optional callback to resolve EQUS string constants by name.
    /// </summary>
    public Func<string, string?>? EqusResolver { get; set; }

    /// <summary>
    /// Optional callback to resolve CHARLEN with the active charmap.
    /// </summary>
    public Func<string, int>? CharlenResolver { get; set; }

    /// <summary>
    /// Optional callback to check if a string is in the active charmap.
    /// </summary>
    public Func<string, bool>? IncharmapResolver { get; set; }

    /// <summary>
    /// Optional callback to read a file for READFILE.
    /// </summary>
    public Func<string, int?, string?>? ReadfileResolver { get; set; }

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
            SyntaxKind.NumberLiteral => ParseNumber(((GreenToken)node).Text),
            SyntaxKind.FixedPointLiteral => ParseFixedPoint(((GreenToken)node).Text, FracBits),
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
                var raw = StripQuotes(token.Text);
                return _resolveInterpolations != null ? _resolveInterpolations(raw) : raw;
            }
        }

        if (node.Kind == SyntaxKind.StringLiteral && node is GreenToken strTok)
            return StripQuotes(strTok.Text);

        // Binary expression: string ++ string (concatenation)
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

        // Function call that returns string
        if (node.Kind == SyntaxKind.FunctionCallExpression)
            return EvaluateStringFunction(node);

        // NameExpression — check EQUS
        if (node.Kind == SyntaxKind.NameExpression)
        {
            var token = (GreenToken)((GreenNode)node).GetChild(0)!;
            if (EqusResolver != null)
                return EqusResolver(token.Text);
        }

        // UnaryExpression with # — EQUS string dereference (#name yields string value)
        if (node.Kind == SyntaxKind.UnaryExpression)
        {
            var green = (GreenNode)node;
            var op = (GreenToken)green.GetChild(0)!;
            if (op.Kind == SyntaxKind.HashToken)
            {
                var operand = green.GetChild(1)!;
                // Get the name from the operand
                string? name = null;
                if (operand.Kind == SyntaxKind.NameExpression)
                    name = ((GreenToken)((GreenNode)operand).GetChild(0)!).Text;
                else if (operand is GreenToken tok)
                    name = tok.Text;
                if (name != null && EqusResolver != null)
                    return EqusResolver(name);
            }
        }

        return null;
    }

    private long? EvaluateLiteral(GreenNodeBase node)
    {
        var token = (GreenToken)((GreenNode)node).GetChild(0)!;
        if (token.Kind == SyntaxKind.FixedPointLiteral)
        {
            var (value, overflow) = ParseFixedPointEx(token.Text, FracBits);
            if (overflow)
                _diagnostics.Report(default,
                    $"Fixed-point literal '{token.Text}' overflows Q.{FracBits} range",
                    DiagnosticSeverity.Warning);
            return value;
        }
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

        // String operators: ===, !==, ++
        if (op.Kind is SyntaxKind.EqualsEqualsEqualsToken or SyntaxKind.TripleEqualsToken
            or SyntaxKind.BangEqualsEqualsToken or SyntaxKind.PlusPlusToken)
        {
            var leftStr = TryEvaluateString(green.GetChild(0)!);
            var rightStr = TryEvaluateString(green.GetChild(2)!);
            if (leftStr != null && rightStr != null)
            {
                return op.Kind switch
                {
                    SyntaxKind.EqualsEqualsEqualsToken or SyntaxKind.TripleEqualsToken =>
                        string.Equals(leftStr, rightStr, StringComparison.Ordinal) ? 1L : 0L,
                    SyntaxKind.BangEqualsEqualsToken =>
                        !string.Equals(leftStr, rightStr, StringComparison.Ordinal) ? 1L : 0L,
                    // ++ returns a string — numeric eval returns null
                    _ => null,
                };
            }
            return null;
        }

        var left = TryEvaluate(green.GetChild(0)!);
        var right = TryEvaluate(green.GetChild(2)!);

        if (left == null || right == null) return null;

        // Treat values as signed 32-bit for division/modulo (C semantics)
        int li = (int)left.Value;
        int ri = (int)right.Value;

        // All arithmetic uses signed 32-bit to match RGBDS behavior
        return op.Kind switch
        {
            SyntaxKind.PlusToken => (long)(int)(li + ri),
            SyntaxKind.MinusToken => (long)(int)(li - ri),
            SyntaxKind.StarToken => (long)(int)(li * ri),
            SyntaxKind.SlashToken when ri != 0 => EvalDivision(li, ri),
            SyntaxKind.SlashToken => null,
            SyntaxKind.PercentToken when ri != 0 => EvalModulo(li, ri),
            SyntaxKind.PercentToken => null,
            SyntaxKind.AmpersandToken => (long)(int)(li & ri),
            SyntaxKind.PipeToken => (long)(int)(li | ri),
            SyntaxKind.CaretToken => (long)(int)(li ^ ri),
            SyntaxKind.LessThanLessThanToken => EvalLeftShift(li, ri),
            SyntaxKind.GreaterThanGreaterThanToken => (long)(li >> Math.Min(ri, 31)),
            SyntaxKind.TripleGreaterThanToken => EvalLogicalRightShift(li, ri),
            SyntaxKind.StarStarToken => EvalExponentiation(li, ri),
            SyntaxKind.EqualsEqualsToken => li == ri ? 1L : 0L,
            SyntaxKind.BangEqualsToken => li != ri ? 1L : 0L,
            SyntaxKind.LessThanToken => li < ri ? 1L : 0L,
            SyntaxKind.GreaterThanToken => li > ri ? 1L : 0L,
            SyntaxKind.LessThanEqualsToken => li <= ri ? 1L : 0L,
            SyntaxKind.GreaterThanEqualsToken => li >= ri ? 1L : 0L,
            SyntaxKind.AmpersandAmpersandToken => (li != 0 && ri != 0) ? 1L : 0L,
            SyntaxKind.PipePipeToken => (li != 0 || ri != 0) ? 1L : 0L,
            _ => null,
        };
    }

    private long EvalDivision(int left, int right)
    {
        // Handle INT_MIN / -1 overflow (undefined in C, crash in C#)
        if (left == int.MinValue && right == -1)
        {
            _diagnostics.Report(default, "Division overflow: INT_MIN / -1",
                DiagnosticSeverity.Warning);
            return (long)left; // RGBDS returns INT_MIN
        }
        return (long)(left / right);
    }

    private long EvalModulo(int left, int right)
    {
        if (left == int.MinValue && right == -1)
            return 0; // INT_MIN % -1 = 0
        return (long)(left % right);
    }

    private long EvalLeftShift(int left, int amount)
    {
        if (amount < 0 || amount >= 32)
        {
            _diagnostics.Report(default, $"Shift amount {amount} is out of range 0-31",
                DiagnosticSeverity.Warning);
            return 0;
        }
        return (long)(left << amount);
    }

    private static long EvalLogicalRightShift(int left, int amount)
    {
        if (amount < 0 || amount >= 32) return 0;
        return (long)((uint)left >> amount);
    }

    private static long EvalExponentiation(int baseVal, int exp)
    {
        if (exp < 0) return 0;
        if (exp == 0) return 1;
        long result = 1;
        long b = baseVal;
        for (int i = 0; i < exp; i++)
            result *= b;
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
    /// Get all arguments from a function call node (children at indices 2, 4, 6, ... before close paren).
    /// </summary>
    private List<GreenNodeBase> GetFunctionArgs(GreenNode green)
    {
        var args = new List<GreenNodeBase>();
        // Children: keyword ( arg , arg , arg ... )
        for (int i = 2; i < green.ChildCount; i++)
        {
            var child = green.GetChild(i)!;
            if (child is GreenToken tok && (tok.Kind == SyntaxKind.CommaToken
                || tok.Kind == SyntaxKind.CloseParenToken
                || tok.Kind == SyntaxKind.OpenParenToken))
                continue;
            args.Add(child);
        }
        return args;
    }

    private long? EvaluateFunction(GreenNodeBase node)
    {
        var green = (GreenNode)node;
        var keyword = (GreenToken)green.GetChild(0)!;
        var args = GetFunctionArgs(green);
        var arg = args.Count > 0 ? args[0] : null;

        return keyword.Kind switch
        {
            SyntaxKind.HighKeyword when arg != null =>
                TryEvaluate(arg) is { } v ? (v >> 8) & 0xFF : null,
            SyntaxKind.LowKeyword when arg != null =>
                TryEvaluate(arg) is { } v ? v & 0xFF : null,
            // BANK, STARTOF are linker-time — always null
            SyntaxKind.BankKeyword => null,
            SyntaxKind.StartofKeyword => null,
            // SIZEOF: check for register operands first, then linker-time
            SyntaxKind.SizeofKeyword => EvaluateSizeof(args),
            // DEF(symbol) — check if defined. Only valid when argument is a NameExpression;
            // any other expression kind (literal, binary, etc.) is not a symbol name reference.
            SyntaxKind.DefKeyword when arg?.Kind == SyntaxKind.NameExpression =>
                _symbols.Lookup(((GreenToken)((GreenNode)arg).GetChild(0)!).Text) is { State: SymbolState.Defined } ? 1L : 0L,
            // ISCONST — return 1 if constant, 0 otherwise
            SyntaxKind.IsConstKeyword when arg != null =>
                TryEvaluate(arg) != null ? 1L : 0L,
            // String functions that return int
            SyntaxKind.StrlenKeyword => EvalStrlen(args),
            SyntaxKind.StrcmpKeyword => EvalStrcmp(args),
            SyntaxKind.StrfindKeyword => EvalStrfind(args),
            SyntaxKind.StrrfindKeyword => EvalStrrfind(args),
            SyntaxKind.BytelenKeyword => EvalBytelen(args),
            SyntaxKind.StrbyteKeyword => EvalStrbyte(args),
            SyntaxKind.CharlenKeyword => EvalCharlen(args),
            SyntaxKind.IncharmapKeyword => EvalIncharmap(args),
            // String-returning functions used in numeric context → null
            SyntaxKind.StrcatKeyword or SyntaxKind.StrsubKeyword
                or SyntaxKind.StruprKeyword or SyntaxKind.StrlwrKeyword
                or SyntaxKind.ReadfileKeyword or SyntaxKind.StrfmtKeyword => null,
            // Math functions (fixed-point)
            SyntaxKind.MulKeyword => EvalMul(args),
            SyntaxKind.DivFuncKeyword or SyntaxKind.DivKeyword => EvalDiv(args),
            SyntaxKind.FmodKeyword => EvalFmod(args),
            SyntaxKind.PowKeyword => EvalPow(args),
            SyntaxKind.LogKeyword => EvalLog(args),
            SyntaxKind.RoundKeyword => EvalRound(args),
            SyntaxKind.CeilKeyword => EvalCeil(args),
            SyntaxKind.FloorKeyword => EvalFloor(args),
            // Trig functions
            SyntaxKind.SinKeyword => EvalSin(args),
            SyntaxKind.CosKeyword => EvalCos(args),
            SyntaxKind.TanKeyword => EvalTan(args),
            SyntaxKind.AsinKeyword => EvalAsin(args),
            SyntaxKind.AcosKeyword => EvalAcos(args),
            SyntaxKind.AtanKeyword => EvalAtan(args),
            SyntaxKind.Atan2Keyword => EvalAtan2(args),
            // Bit query
            SyntaxKind.BitwidthKeyword => EvalBitwidth(args),
            SyntaxKind.TzcountKeyword => EvalTzcount(args),
            _ => null,
        };
    }

    /// <summary>
    /// Evaluate string-returning functions.
    /// </summary>
    private string? EvaluateStringFunction(GreenNodeBase node)
    {
        var green = (GreenNode)node;
        var keyword = (GreenToken)green.GetChild(0)!;
        var args = GetFunctionArgs(green);

        return keyword.Kind switch
        {
            SyntaxKind.StrcatKeyword => EvalStrcat(args),
            SyntaxKind.StrsubKeyword => EvalStrsub(args),
            SyntaxKind.StruprKeyword => EvalStrupr(args),
            SyntaxKind.StrlwrKeyword => EvalStrlwr(args),
            SyntaxKind.ReadfileKeyword => EvalReadfile(args),
            _ => null,
        };
    }

    // =========================================================================
    // SIZEOF for register operands
    // =========================================================================

    private long? EvaluateSizeof(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var arg = args[0];

        // Check for register name (NameExpression or raw token)
        string? regName = null;
        if (arg.Kind == SyntaxKind.NameExpression)
        {
            var token = (GreenToken)((GreenNode)arg).GetChild(0)!;
            regName = token.Text;
        }
        else if (arg is GreenToken tok)
        {
            regName = tok.Text;
        }
        // Check for string argument (section name) — linker-time
        if (arg.Kind == SyntaxKind.LiteralExpression)
        {
            var litTok = (GreenToken)((GreenNode)arg).GetChild(0)!;
            if (litTok.Kind == SyntaxKind.StringLiteral)
                return null; // linker-time
            // It might be a register keyword parsed as literal
            regName = litTok.Text;
        }

        if (regName != null)
        {
            return regName.ToLowerInvariant() switch
            {
                "a" or "b" or "c" or "d" or "e" or "h" or "l" or "f" => 1,
                "af" or "bc" or "de" or "hl" or "sp" => 2,
                _ => null,
            };
        }

        // Indirect operand [bc], [hl+], [hld] — always 1 byte
        if (arg.Kind == SyntaxKind.IndirectOperand)
            return 1;

        // sizeof(high(af)), sizeof(low(bc)) — always 1
        if (arg.Kind == SyntaxKind.FunctionCallExpression)
        {
            var innerGreen = (GreenNode)arg;
            var innerKw = (GreenToken)innerGreen.GetChild(0)!;
            if (innerKw.Kind is SyntaxKind.HighKeyword or SyntaxKind.LowKeyword)
                return 1;
        }

        return null;
    }

    // =========================================================================
    // String functions (return long)
    // =========================================================================

    private long? EvalStrlen(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var s = TryEvaluateString(args[0]);
        return s != null ? s.Length : null;
    }

    private long? EvalStrcmp(List<GreenNodeBase> args)
    {
        if (args.Count < 2) return null;
        var s1 = TryEvaluateString(args[0]);
        var s2 = TryEvaluateString(args[1]);
        if (s1 != null && s2 != null)
            return string.Compare(s1, s2, StringComparison.Ordinal);
        return null;
    }

    private long? EvalStrfind(List<GreenNodeBase> args)
    {
        if (args.Count < 2) return null;
        var haystack = TryEvaluateString(args[0]);
        var needle = TryEvaluateString(args[1]);
        if (haystack == null || needle == null) return null;
        if (needle.Length == 0) return 0;
        int idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        return idx < 0 ? -1 : idx;
    }

    private long? EvalStrrfind(List<GreenNodeBase> args)
    {
        if (args.Count < 2) return null;
        var haystack = TryEvaluateString(args[0]);
        var needle = TryEvaluateString(args[1]);
        if (haystack == null || needle == null) return null;
        if (needle.Length == 0) return haystack.Length;
        int idx = haystack.LastIndexOf(needle, StringComparison.Ordinal);
        return idx < 0 ? -1 : idx;
    }

    private long? EvalBytelen(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var s = TryEvaluateString(args[0]);
        if (s == null) return null;
        if (_charMaps != null)
            return _charMaps.EncodeString(s).Length;
        return System.Text.Encoding.UTF8.GetByteCount(s);
    }

    private long? EvalStrbyte(List<GreenNodeBase> args)
    {
        if (args.Count < 2) return null;
        var s = TryEvaluateString(args[0]);
        var idx = TryEvaluate(args[1]);
        if (s == null || idx == null) return null;
        var bytes = _charMaps != null ? _charMaps.EncodeString(s) : System.Text.Encoding.UTF8.GetBytes(s);
        int i = (int)idx.Value;
        // Negative index: from end
        if (i < 0) i = bytes.Length + i;
        if (i < 0 || i >= bytes.Length)
        {
            _diagnostics.Report(default, $"STRBYTE index {idx.Value} out of range for string of length {bytes.Length}",
                DiagnosticSeverity.Warning);
            return 0;
        }
        return bytes[i];
    }

    private long? EvalCharlen(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var s = TryEvaluateString(args[0]);
        if (s == null) return null;
        if (_charMaps != null)
            return _charMaps.CountChars(s);
        if (CharlenResolver != null) return CharlenResolver(s);
        return s.Length; // fallback: 1 char per byte
    }

    private long? EvalIncharmap(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var s = TryEvaluateString(args[0]);
        if (s == null) return null;
        if (string.IsNullOrEmpty(s)) return 0;
        if (_charMaps != null)
            return _charMaps.HasMapping(s) ? 1L : 0L;
        if (IncharmapResolver != null) return IncharmapResolver(s) ? 1L : 0L;
        return 0;
    }

    // =========================================================================
    // String functions (return string)
    // =========================================================================

    private string? EvalStrcat(List<GreenNodeBase> args)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var arg in args)
        {
            var s = TryEvaluateString(arg);
            if (s == null) return null;
            sb.Append(s);
        }
        return sb.ToString();
    }

    private string? EvalStrsub(List<GreenNodeBase> args)
    {
        if (args.Count < 2) return null;
        var s = TryEvaluateString(args[0]);
        var start = TryEvaluate(args[1]);
        if (s == null || start == null) return null;
        int startIdx = (int)start.Value;
        // RGBDS STRSUB is 1-based
        startIdx -= 1;
        if (startIdx < 0) startIdx = 0;
        if (startIdx >= s.Length) return "";
        if (args.Count >= 3)
        {
            var len = TryEvaluate(args[2]);
            if (len == null) return null;
            int length = (int)len.Value;
            if (startIdx + length > s.Length) length = s.Length - startIdx;
            return s.Substring(startIdx, length);
        }
        return s.Substring(startIdx);
    }

    private string? EvalStrupr(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var s = TryEvaluateString(args[0]);
        return s?.ToUpperInvariant();
    }

    private string? EvalStrlwr(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var s = TryEvaluateString(args[0]);
        return s?.ToLowerInvariant();
    }

    private string? EvalReadfile(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var path = TryEvaluateString(args[0]);
        if (path == null) return null;
        int? limit = null;
        if (args.Count >= 2)
        {
            var lv = TryEvaluate(args[1]);
            if (lv.HasValue) limit = (int)lv.Value;
        }
        if (ReadfileResolver != null)
            return ReadfileResolver(path, limit);
        _diagnostics.Report(default, $"Cannot read file '{path}': no file resolver available");
        return null;
    }

    // =========================================================================
    // Fixed-point math functions
    // =========================================================================

    private double ToDouble(long fixedVal) => (double)fixedVal / (1L << FracBits);
    private long ToFixed(double val)
    {
        double scale = 1L << FracBits;
        double scaled = val * scale;
        // Check for overflow
        if (scaled > int.MaxValue || scaled < int.MinValue)
        {
            _diagnostics.Report(default, $"Fixed-point value {val} overflows Q.{FracBits} range",
                DiagnosticSeverity.Warning);
            return (long)(int)(scaled > 0 ? int.MaxValue : int.MinValue);
        }
        return (long)(int)Math.Round(scaled);
    }

    private long? EvalMul(List<GreenNodeBase> args)
    {
        if (args.Count < 2) return null;
        var a = TryEvaluate(args[0]);
        var b = TryEvaluate(args[1]);
        if (a == null || b == null) return null;
        // Fixed-point multiplication: (a * b) >> fracBits
        long result = ((long)(int)a.Value * (int)b.Value) >> FracBits;
        return (long)(int)result;
    }

    private long? EvalDiv(List<GreenNodeBase> args)
    {
        if (args.Count < 2) return null;
        var a = TryEvaluate(args[0]);
        var b = TryEvaluate(args[1]);
        if (a == null || b == null) return null;
        int av = (int)a.Value;
        int bv = (int)b.Value;
        if (bv == 0)
        {
            if (av == 0) return 0; // NaN -> 0
            return av > 0 ? 0x7fffffff : unchecked((long)(int)0x80000000);
        }
        // Fixed-point division: (a << fracBits) / b
        long result = ((long)av << FracBits) / bv;
        return (long)(int)result;
    }

    private long? EvalFmod(List<GreenNodeBase> args)
    {
        if (args.Count < 2) return null;
        var a = TryEvaluate(args[0]);
        var b = TryEvaluate(args[1]);
        if (a == null || b == null) return null;
        if ((int)b.Value == 0) return 0; // NaN
        double da = ToDouble((int)a.Value);
        double db = ToDouble((int)b.Value);
        return ToFixed(da % db);
    }

    private long? EvalPow(List<GreenNodeBase> args)
    {
        if (args.Count < 2) return null;
        var a = TryEvaluate(args[0]);
        var b = TryEvaluate(args[1]);
        if (a == null || b == null) return null;
        double da = ToDouble((int)a.Value);
        double db = ToDouble((int)b.Value);
        return ToFixed(Math.Pow(da, db));
    }

    private long? EvalLog(List<GreenNodeBase> args)
    {
        if (args.Count < 2) return null;
        var a = TryEvaluate(args[0]);
        var b = TryEvaluate(args[1]);
        if (a == null || b == null) return null;
        double da = ToDouble((int)a.Value);
        double db = ToDouble((int)b.Value);
        return ToFixed(Math.Log(da, db));
    }

    private long? EvalRound(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var a = TryEvaluate(args[0]);
        if (a == null) return null;
        double da = ToDouble((int)a.Value);
        return ToFixed(Math.Round(da, MidpointRounding.AwayFromZero));
    }

    private long? EvalCeil(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var a = TryEvaluate(args[0]);
        if (a == null) return null;
        double da = ToDouble((int)a.Value);
        return ToFixed(Math.Ceiling(da));
    }

    private long? EvalFloor(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var a = TryEvaluate(args[0]);
        if (a == null) return null;
        double da = ToDouble((int)a.Value);
        return ToFixed(Math.Floor(da));
    }

    // =========================================================================
    // Trig functions — input/output in fixed-point, angle in turns (0..1 = 0..360 degrees)
    // =========================================================================

    private long? EvalSin(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var a = TryEvaluate(args[0]);
        if (a == null) return null;
        double turns = ToDouble((int)a.Value);
        return ToFixed(Math.Sin(turns * 2 * Math.PI));
    }

    private long? EvalCos(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var a = TryEvaluate(args[0]);
        if (a == null) return null;
        double turns = ToDouble((int)a.Value);
        return ToFixed(Math.Cos(turns * 2 * Math.PI));
    }

    private long? EvalTan(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var a = TryEvaluate(args[0]);
        if (a == null) return null;
        double turns = ToDouble((int)a.Value);
        return ToFixed(Math.Tan(turns * 2 * Math.PI));
    }

    private long? EvalAsin(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var a = TryEvaluate(args[0]);
        if (a == null) return null;
        double val = ToDouble((int)a.Value);
        return ToFixed(Math.Asin(val) / (2 * Math.PI));
    }

    private long? EvalAcos(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var a = TryEvaluate(args[0]);
        if (a == null) return null;
        double val = ToDouble((int)a.Value);
        return ToFixed(Math.Acos(val) / (2 * Math.PI));
    }

    private long? EvalAtan(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var a = TryEvaluate(args[0]);
        if (a == null) return null;
        double val = ToDouble((int)a.Value);
        return ToFixed(Math.Atan(val) / (2 * Math.PI));
    }

    private long? EvalAtan2(List<GreenNodeBase> args)
    {
        if (args.Count < 2) return null;
        var a = TryEvaluate(args[0]);
        var b = TryEvaluate(args[1]);
        if (a == null || b == null) return null;
        double y = ToDouble((int)a.Value);
        double x = ToDouble((int)b.Value);
        return ToFixed(Math.Atan2(y, x) / (2 * Math.PI));
    }

    // =========================================================================
    // Bit query functions
    // =========================================================================

    private long? EvalBitwidth(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var a = TryEvaluate(args[0]);
        if (a == null) return null;
        int v = (int)a.Value;
        if (v == 0) return 0;
        // For negative values, use unsigned representation
        uint uv = (uint)v;
        return 32 - BitOperations.LeadingZeroCount(uv);
    }

    private long? EvalTzcount(List<GreenNodeBase> args)
    {
        if (args.Count == 0) return null;
        var a = TryEvaluate(args[0]);
        if (a == null) return null;
        uint v = (uint)(int)a.Value;
        if (v == 0) return 32;
        return BitOperations.TrailingZeroCount(v);
    }

    // =========================================================================
    // String helpers
    // =========================================================================

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
                    // Line continuation in string: \<newline><leading whitespace> removed
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

    // =========================================================================
    // Number parsing
    // =========================================================================

    public long? ParseNumberWithFixedPoint(string text) => ParseNumber(text, _fracBits);

    public static long? ParseNumber(string text) => ParseNumber(text, 0);

    public static long? ParseNumber(string text, int fracBits)
    {
        string clean = text.Replace("_", "");

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

        if (long.TryParse(clean, out var val))
            return val;
        return null;
    }

    /// <summary>
    /// Parse a fixed-point literal (e.g. "3.14159") into a Q.N fixed-point integer.
    /// Returns the value and an overflow flag.
    /// </summary>
    public static (long value, bool overflow) ParseFixedPointEx(string text, int fracBits)
    {
        string clean = text.Replace("_", "");
        if (!double.TryParse(clean, System.Globalization.CultureInfo.InvariantCulture, out var dval))
            return (0, false);
        double scale = 1L << fracBits;
        double scaled = dval * scale;
        bool overflow = scaled > int.MaxValue || scaled < int.MinValue;
        return ((long)(int)(long)Math.Round(scaled), overflow);
    }

    public static long? ParseFixedPoint(string text, int fracBits)
    {
        var (value, _) = ParseFixedPointEx(text, fracBits);
        return value;
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
