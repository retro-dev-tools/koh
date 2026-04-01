using Koh.Core.Diagnostics;
using Koh.Core.Symbols;

namespace Koh.Core.Binding;

/// <summary>
/// Resolves {symbol} and {fmt:symbol} string interpolations.
/// Handles numeric formatting, EQUS lookup, SECTION() references, and @ (current PC).
/// </summary>
internal sealed class InterpolationResolver
{
    private readonly SymbolTable _symbols;
    private readonly Dictionary<string, string> _equsConstants;
    private readonly DiagnosticBag _diagnostics;

    internal Func<int>? GetCurrentPC { get; set; }
    internal Func<string, string?>? SectionNameResolver { get; set; }
    internal string? CurrentSectionName { get; set; }

    internal InterpolationResolver(SymbolTable symbols, Dictionary<string, string> equsConstants,
        DiagnosticBag diagnostics)
    {
        _symbols = symbols;
        _equsConstants = equsConstants;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Resolve {symbol} and {fmt:symbol} interpolations in a string.
    /// Supports format specifiers (d, u, x, X, b, o) with optional # prefix,
    /// +/- sign, zero-padding, and width.
    /// </summary>
    internal string Resolve(string text)
    {
        if (!text.Contains('{')) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '{')
            {
                // \{ — escaped brace, literal {
                sb.Append('{');
                i += 2;
                continue;
            }

            if (text[i] == '{')
            {
                int braceStart = i;
                i++; // skip {
                // Parse optional format specifier: {fmt:name} or {name}
                string? fmt = null;
                int colonPos = text.IndexOf(':', i);
                int closePos = text.IndexOf('}', i);

                if (closePos < 0)
                {
                    // Unclosed { — emit as-is
                    sb.Append('{');
                    continue;
                }

                if (colonPos >= 0 && colonPos < closePos)
                {
                    fmt = text[i..colonPos];
                    i = colonPos + 1;
                }

                string name = text[i..closePos];
                i = closePos + 1;

                // Handle SECTION(...) as a special function in interpolation
                string trimmedName = name.Trim();
                if (fmt == null && trimmedName.StartsWith("SECTION(", StringComparison.OrdinalIgnoreCase)
                    && trimmedName.EndsWith(")"))
                {
                    string arg = trimmedName.Substring(8, trimmedName.Length - 9).Trim();
                    string? sectionName = SectionNameResolver?.Invoke(arg);
                    if (sectionName != null)
                    {
                        sb.Append(sectionName);
                        continue;
                    }
                }

                // Validate format specifier if present
                string? trimmedFmt = fmt?.Trim();
                if (trimmedFmt != null && !IsValidFormat(trimmedFmt))
                {
                    _diagnostics.Report(default,
                        $"Invalid format specifier '{trimmedFmt}' in string interpolation");
                    sb.Append(text[braceStart..i]);
                    continue;
                }

                // Resolve the symbol
                string? resolved = ResolveValue(trimmedName, trimmedFmt);
                if (resolved != null)
                    sb.Append(resolved);
                else
                {
                    // Unknown symbol — preserve original text
                    sb.Append(text[braceStart..i]);
                    _diagnostics.Report(default, $"Interpolation: symbol '{name.Trim()}' not found");
                }
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    private string? ResolveValue(string name, string? fmt)
    {
        // Handle @ (current PC) — RGBDS defaults to uppercase hex with $ prefix
        if (name == "@" && GetCurrentPC != null)
            return FormatNumericValue(GetCurrentPC(), fmt ?? "#X");

        // SECTION() function: SECTION(@) returns current section, SECTION(label) returns label's section
        if (name.StartsWith("SECTION(", StringComparison.OrdinalIgnoreCase) && name.EndsWith(')'))
        {
            var arg = name["SECTION(".Length..^1].Trim();
            if (arg == "@")
                return CurrentSectionName ?? "";
            // SECTION(label) — look up label's section
            var labelSym = _symbols.Lookup(arg);
            if (labelSym?.Section != null)
                return labelSym.Section;
            return null;
        }

        // Check EQUS constants first (string type)
        if (_equsConstants.TryGetValue(name, out var equsValue))
            return equsValue; // EQUS always returns raw string regardless of format

        // Check numeric symbols
        var sym = _symbols.Lookup(name);
        if (sym != null && sym.State == SymbolState.Defined)
        {
            long val = sym.Value;
            return FormatNumericValue((int)val, fmt ?? "d");
        }

        // Numeric literal in interpolation: {d:5}, {x:42}, etc.
        // This arises when macro args like \1 are eagerly substituted before interpolation,
        // producing e.g. PRINTLN "{d:5}" where 5 is a number, not a symbol name.
        var parsedNum = ExpressionEvaluator.ParseNumber(name);
        if (parsedNum.HasValue)
            return FormatNumericValue((int)parsedNum.Value, fmt ?? "d");

        return null;
    }

    /// <summary>
    /// Format a numeric value according to an RGBDS format specifier.
    /// Format: [+][#][0][width][type] where type is d/u/x/X/b/o/f
    /// </summary>
    internal static string FormatNumericValue(int val, string? fmt)
    {
        if (string.IsNullOrEmpty(fmt))
            return val.ToString();

        // Parse format spec: [+][#][0][width][type]
        int pos = 0;
        bool showSign = false;
        bool hasPrefix = false;
        bool zeroPad = false;
        int width = 0;

        if (pos < fmt.Length && fmt[pos] == '+')
        {
            showSign = true;
            pos++;
        }
        if (pos < fmt.Length && fmt[pos] == '#')
        {
            hasPrefix = true;
            pos++;
        }
        if (pos < fmt.Length && fmt[pos] == '0')
        {
            zeroPad = true;
            pos++;
        }
        // Parse width digits
        int widthStart = pos;
        while (pos < fmt.Length && char.IsDigit(fmt[pos]))
            pos++;
        if (pos > widthStart)
            int.TryParse(fmt.AsSpan(widthStart, pos - widthStart), out width);

        // Remaining is the type character
        string type = pos < fmt.Length ? fmt[pos..] : "d";

        string prefix = "";
        if (hasPrefix)
        {
            prefix = type switch
            {
                "x" or "X" => "$",
                "b" => "%",
                "o" => "&",
                _ => "",
            };
        }

        string signStr = "";
        if (showSign && val >= 0)
            signStr = "+";

        string formatted = type switch
        {
            "d" => val.ToString(),
            "u" => ((uint)val).ToString(),
            "x" => ((uint)val).ToString("x"),
            "X" => ((uint)val).ToString("X"),
            "b" => Convert.ToString((uint)val, 2),
            "o" => Convert.ToString((uint)val, 8),
            _ => val.ToString(),
        };

        // Apply width padding — width includes prefix and sign
        int totalExtra = prefix.Length + signStr.Length;
        if (width > 0 && formatted.Length + totalExtra < width)
        {
            char padChar = zeroPad ? '0' : ' ';
            formatted = formatted.PadLeft(width - totalExtra, padChar);
        }

        return signStr + prefix + formatted;
    }

    /// <summary>
    /// Returns true if the interpolation format specifier is valid.
    /// Valid forms: [+][#][0][width]type  where type is d/u/x/X/b/o/f/s
    /// </summary>
    private static bool IsValidFormat(string fmt)
    {
        if (string.IsNullOrEmpty(fmt)) return true;

        int pos = 0;
        // Optional sign flag
        if (pos < fmt.Length && fmt[pos] == '+') pos++;
        // Optional prefix flag
        if (pos < fmt.Length && fmt[pos] == '#') pos++;
        // Optional zero-pad flag
        if (pos < fmt.Length && fmt[pos] == '0') pos++;
        // Optional width digits
        while (pos < fmt.Length && char.IsDigit(fmt[pos])) pos++;
        // Must have exactly one type character remaining
        if (pos >= fmt.Length) return false; // no type
        char type = fmt[pos++];
        if (pos != fmt.Length) return false; // extra chars after type
        return type is 'd' or 'u' or 'x' or 'X' or 'b' or 'o' or 'f' or 's';
    }
}
