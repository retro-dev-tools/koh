namespace Koh.Core.Syntax;

/// <summary>
/// Result of resolving an interpolated symbol name.
/// </summary>
public abstract record InterpolationResult
{
    /// <summary>Symbol resolved successfully. Insert expandedText into the source stream.</summary>
    public sealed record Success(string ExpandedText) : InterpolationResult;

    /// <summary>Symbol does not exist. Fatal error in RGBDS — assembly should abort.</summary>
    public sealed record NotFound(string Name) : InterpolationResult;

    /// <summary>Format failure, wrong symbol kind, or other resolution error.</summary>
    public sealed record Error(string Message) : InterpolationResult;
}

/// <summary>
/// Parsed format specifier from {fmt:symbol} syntax per RGBDS spec.
/// </summary>
public sealed class InterpolationFormat
{
    public char? Sign { get; init; }       // '+' or ' ' or null
    public bool Exact { get; init; }       // '#' — base prefix for integers
    public bool LeftAlign { get; init; }   // '-'
    public bool ZeroPad { get; init; }     // '0'
    public int? Width { get; init; }
    public int? FracDigits { get; init; }  // after '.'
    public int? FixedPrec { get; init; }   // after 'q'
    public char Type { get; init; }        // d/u/x/X/b/o/f/s (required)

    /// <summary>
    /// Parses an RGBDS format specifier string (everything before the ':' in {fmt:name}).
    /// Returns null if the format string is empty or invalid.
    /// </summary>
    public static InterpolationFormat? Parse(ReadOnlySpan<char> fmt)
    {
        if (fmt.IsEmpty) return null;

        int i = 0;
        char? sign = null;
        bool exact = false;
        bool leftAlign = false;
        bool zeroPad = false;
        int? width = null;
        int? fracDigits = null;
        int? fixedPrec = null;

        // Sign
        if (i < fmt.Length && fmt[i] is '+' or ' ')
            sign = fmt[i++];

        // Exact (#)
        if (i < fmt.Length && fmt[i] == '#')
        {
            exact = true;
            i++;
        }

        // Align (-)
        if (i < fmt.Length && fmt[i] == '-')
        {
            leftAlign = true;
            i++;
        }

        // Pad (0)
        if (i < fmt.Length && fmt[i] == '0')
        {
            zeroPad = true;
            i++;
        }

        // Width (digits)
        if (i < fmt.Length && char.IsAsciiDigit(fmt[i]))
        {
            int w = 0;
            while (i < fmt.Length && char.IsAsciiDigit(fmt[i]))
                w = w * 10 + (fmt[i++] - '0');
            width = w;
        }

        // Frac digits (.N)
        if (i < fmt.Length && fmt[i] == '.')
        {
            i++;
            int f = 0;
            while (i < fmt.Length && char.IsAsciiDigit(fmt[i]))
                f = f * 10 + (fmt[i++] - '0');
            fracDigits = f;
        }

        // Fixed precision (qN)
        if (i < fmt.Length && fmt[i] == 'q')
        {
            i++;
            int q = 0;
            while (i < fmt.Length && char.IsAsciiDigit(fmt[i]))
                q = q * 10 + (fmt[i++] - '0');
            fixedPrec = q;
        }

        // Type (required, must be last character)
        if (i >= fmt.Length) return null;
        char type = fmt[i++];
        if (type is not ('d' or 'u' or 'x' or 'X' or 'b' or 'o' or 'f' or 's'))
            return null;

        // Must have consumed everything
        if (i != fmt.Length) return null;

        return new InterpolationFormat
        {
            Sign = sign,
            Exact = exact,
            LeftAlign = leftAlign,
            ZeroPad = zeroPad,
            Width = width,
            FracDigits = fracDigits,
            FixedPrec = fixedPrec,
            Type = type,
        };
    }
}

/// <summary>
/// Interface for resolving interpolated symbol names. Implemented by the
/// semantic environment (expander/binder context) that has access to current
/// EQUS constants and numeric symbols.
/// </summary>
public interface IInterpolationResolver
{
    /// <summary>
    /// Resolve a symbol name with an optional format specifier.
    /// The resolver looks up the symbol, applies formatting for numeric values,
    /// and returns the final text to insert into the source stream.
    /// </summary>
    InterpolationResult Resolve(string name, InterpolationFormat? format);
}
