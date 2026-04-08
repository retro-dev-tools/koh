namespace Koh.Core.Syntax;

/// <summary>
/// A character read from a source, paired with its provenance information.
/// Value is -1 for EOF.
/// </summary>
public readonly record struct SourceChar(int Value, SourceOrigin Origin)
{
    public bool IsEof => Value < 0;
    public char Char => (char)Value;
}

/// <summary>
/// Tracks where a character came from — raw source or interpolation expansion.
/// </summary>
public sealed class SourceOrigin
{
    public static readonly SourceOrigin Eof = new("", -1, null);

    public string FilePath { get; }
    public int Offset { get; }
    public InterpolationFrame? InterpolationFrame { get; }

    public SourceOrigin(string filePath, int offset, InterpolationFrame? expansionFrame)
    {
        FilePath = filePath;
        Offset = offset;
        InterpolationFrame = expansionFrame;
    }
}

/// <summary>
/// Provenance frame for interpolation expansion. Forms a chain for nested expansions.
/// </summary>
public sealed class InterpolationFrame
{
    /// <summary>The span of the {…} in the original source that triggered this expansion.</summary>
    public TextSpan TriggerSpan { get; }

    /// <summary>The text that the interpolation resolved to.</summary>
    public string ExpandedText { get; }

    /// <summary>Parent frame for nested interpolation (inner expansion that produced this one).</summary>
    public InterpolationFrame? Parent { get; }

    public InterpolationFrame(TextSpan triggerSpan, string expandedText, InterpolationFrame? parent)
    {
        TriggerSpan = triggerSpan;
        ExpandedText = expandedText;
        Parent = parent;
    }
}

/// <summary>
/// Abstract character source consumed by the Lexer. Supports interpolation-aware
/// implementations that expand {symbol} before the Lexer sees the characters.
/// </summary>
public interface ICharSource
{
    /// <summary>
    /// Returns the next character without advancing. Side-effect-free from the
    /// caller's perspective: repeated calls return the same SourceChar until
    /// Read() is called.
    /// </summary>
    SourceChar Peek();

    /// <summary>
    /// Returns the next character and advances the position.
    /// </summary>
    SourceChar Read();

    /// <summary>
    /// Signals that the lexer has entered a double-quoted string literal.
    /// In string mode, \{ is treated as a literal escape and does not trigger interpolation.
    /// </summary>
    void EnterDoubleQuotedStringMode();

    /// <summary>
    /// Signals that the lexer has exited a double-quoted string literal.
    /// </summary>
    void ExitDoubleQuotedStringMode();
}

/// <summary>
/// Plain text character source with no interpolation. Used for standalone parsing
/// (tests, IDE features without semantic context).
/// EnterDoubleQuotedStringMode/ExitDoubleQuotedStringMode are no-ops.
/// </summary>
public sealed class StringCharSource : ICharSource
{
    private readonly string _text;
    private readonly string _filePath;
    private int _position;

    public StringCharSource(string text, string filePath = "")
    {
        _text = text;
        _filePath = filePath;
    }

    public SourceChar Peek()
    {
        if (_position >= _text.Length)
            return new SourceChar(-1, SourceOrigin.Eof);
        return new SourceChar(_text[_position], new SourceOrigin(_filePath, _position, null));
    }

    public SourceChar Read()
    {
        var result = Peek();
        if (!result.IsEof)
            _position++;
        return result;
    }

    public void EnterDoubleQuotedStringMode() { }
    public void ExitDoubleQuotedStringMode() { }
}
