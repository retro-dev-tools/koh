using Koh.Core.Diagnostics;

namespace Koh.Core.Syntax;

/// <summary>
/// Character source that intercepts {symbol} interpolation syntax and expands it
/// using an <see cref="IInterpolationResolver"/> before the lexer sees the characters.
/// Matches RGBDS behavior: interpolation fires everywhere (not just inside strings),
/// expanded text is re-expanded, and nested interpolation is supported.
/// </summary>
public sealed class InterpolationAwareSource : ICharSource
{
    private const int MaxDepth = 64;

    private readonly ICharSource _inner;
    private readonly IInterpolationResolver _resolver;
    private readonly DiagnosticBag _diagnostics;

    /// <summary>Stack of expansion frames. Top frame is read first.</summary>
    private readonly Stack<ExpansionReader> _expansionStack = new();

    private bool _inStringMode;

    /// <summary>Cached result for Peek() idempotency.</summary>
    private SourceChar? _peeked;

    public InterpolationAwareSource(ICharSource inner, IInterpolationResolver resolver,
        DiagnosticBag diagnostics)
    {
        _inner = inner;
        _resolver = resolver;
        _diagnostics = diagnostics;
    }

    public SourceChar Peek()
    {
        if (_peeked.HasValue)
            return _peeked.Value;

        _peeked = ReadNext();
        return _peeked.Value;
    }

    public SourceChar Read()
    {
        if (_peeked.HasValue)
        {
            var result = _peeked.Value;
            _peeked = null;
            return result;
        }

        return ReadNext();
    }

    public void EnterDoubleQuotedStringMode()
    {
        _inStringMode = true;
        _inner.EnterDoubleQuotedStringMode();
    }

    public void ExitDoubleQuotedStringMode()
    {
        _inStringMode = false;
        _inner.ExitDoubleQuotedStringMode();
    }

    private SourceChar ReadNext()
    {
        // Read from topmost expansion frame first
        while (_expansionStack.Count > 0)
        {
            var frame = _expansionStack.Peek();
            if (frame.Position < frame.Text.Length)
            {
                char c = frame.Text[frame.Position];
                var origin = new SourceOrigin(
                    frame.Frame.TriggerSpan.Start >= 0 ? "" : "",
                    frame.Frame.TriggerSpan.Start,
                    frame.Frame);
                frame.Position++;

                // Check for nested interpolation in expanded text
                if (c == '{' && !_inStringMode)
                    return TryExpand(c, origin);

                return new SourceChar(c, origin);
            }

            _expansionStack.Pop();
        }

        // Read from inner source
        var sc = _inner.Read();
        if (sc.IsEof)
            return sc;

        // In string mode, \{ suppresses interpolation
        if (_inStringMode && sc.Char == '\\')
        {
            var next = _inner.Peek();
            if (!next.IsEof && next.Char == '{')
            {
                // Emit the backslash; the { will be emitted on next Read()
                return sc;
            }
        }

        // Check for interpolation trigger
        if (sc.Char == '{' && !_inStringMode)
            return TryExpand(sc.Char, sc.Origin);

        return sc;
    }

    private SourceChar TryExpand(char openBrace, SourceOrigin braceOrigin)
    {
        if (_expansionStack.Count >= MaxDepth)
        {
            _diagnostics.Report(default,
                $"Interpolation depth limit ({MaxDepth}) exceeded");
            return new SourceChar(openBrace, braceOrigin);
        }

        // Parse the interpolation content: {[fmt:]name}
        var (name, format, success) = ParseInterpolation(braceOrigin);
        if (!success)
        {
            // Malformed syntax — return { as literal
            return new SourceChar(openBrace, braceOrigin);
        }

        var result = _resolver.Resolve(name, format);

        switch (result)
        {
            case InterpolationResult.Success s:
            {
                if (s.ExpandedText.Length == 0)
                    return ReadNext(); // Empty expansion — skip to next char

                var frame = new InterpolationFrame(
                    new TextSpan(braceOrigin.Offset, 0),
                    s.ExpandedText,
                    braceOrigin.InterpolationFrame);

                _expansionStack.Push(new ExpansionReader(s.ExpandedText, frame));

                // Read first char from the expansion
                return ReadNext();
            }

            case InterpolationResult.NotFound nf:
                _diagnostics.Report(default,
                    $"Interpolated symbol '{nf.Name}' does not exist");
                // RGBDS: fatal error, assembly aborts. We report and return EOF-like
                // to signal the caller that expansion failed.
                return ReadNext();

            case InterpolationResult.Error err:
                _diagnostics.Report(default, err.Message);
                return ReadNext();

            default:
                return ReadNext();
        }
    }

    /// <summary>
    /// Parse interpolation content between { and }. Handles nested braces and
    /// format/name splitting at top-level colon only.
    /// Returns (name, format, success).
    /// </summary>
    private (string Name, InterpolationFormat? Format, bool Success) ParseInterpolation(
        SourceOrigin triggerOrigin)
    {
        var buffer = new System.Text.StringBuilder();
        int depth = 0;
        int colonPos = -1; // position of top-level ':' separator

        while (true)
        {
            SourceChar sc;

            // Read from expansion stack or inner source
            if (_expansionStack.Count > 0)
            {
                var frame = _expansionStack.Peek();
                if (frame.Position < frame.Text.Length)
                {
                    char c = frame.Text[frame.Position];
                    sc = new SourceChar(c, new SourceOrigin("", frame.Frame.TriggerSpan.Start, frame.Frame));
                    frame.Position++;
                }
                else
                {
                    _expansionStack.Pop();
                    continue;
                }
            }
            else
            {
                sc = _inner.Read();
            }

            if (sc.IsEof)
            {
                _diagnostics.Report(default, "Missing '}'");
                return ("", null, false);
            }

            char ch = sc.Char;

            if (ch == '{')
            {
                // Nested interpolation — resolve it recursively
                depth++;
                if (_expansionStack.Count >= MaxDepth)
                {
                    _diagnostics.Report(default,
                        $"Interpolation depth limit ({MaxDepth}) exceeded");
                    return ("", null, false);
                }

                var (innerName, innerFmt, innerOk) = ParseInterpolation(sc.Origin);
                if (!innerOk)
                    return ("", null, false);

                var innerResult = _resolver.Resolve(innerName, innerFmt);
                if (innerResult is InterpolationResult.Success innerSuccess)
                {
                    buffer.Append(innerSuccess.ExpandedText);
                }
                else if (innerResult is InterpolationResult.NotFound nf)
                {
                    _diagnostics.Report(default,
                        $"Interpolated symbol '{nf.Name}' does not exist");
                    return ("", null, false);
                }
                else if (innerResult is InterpolationResult.Error err)
                {
                    _diagnostics.Report(default, err.Message);
                    return ("", null, false);
                }

                depth--;
                continue;
            }

            if (ch == '}')
            {
                break; // End of this interpolation
            }

            if (ch == ':' && depth == 0 && colonPos < 0)
            {
                colonPos = buffer.Length;
            }

            buffer.Append(ch);
        }

        var content = buffer.ToString();

        if (colonPos >= 0)
        {
            var fmtStr = content.AsSpan(0, colonPos);
            var name = content[(colonPos + 1)..];
            var format = InterpolationFormat.Parse(fmtStr);
            return (name, format, true);
        }

        return (content, null, true);
    }

    /// <summary>Internal reader for an expansion frame.</summary>
    private sealed class ExpansionReader
    {
        public string Text { get; }
        public InterpolationFrame Frame { get; }
        public int Position { get; set; }

        public ExpansionReader(string text, InterpolationFrame frame)
        {
            Text = text;
            Frame = frame;
        }
    }
}
