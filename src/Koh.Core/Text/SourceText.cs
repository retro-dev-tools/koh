using Koh.Core.Syntax;

namespace Koh.Core.Text;

public sealed class SourceText
{
    private readonly string _text;
    private readonly TextLine[] _lines;

    public int Length => _text.Length;
    public char this[int index] => _text[index];
    public string FilePath { get; }
    public IReadOnlyList<TextLine> Lines => _lines;

    private SourceText(string text, string filePath = "")
    {
        _text = text;
        FilePath = filePath;
        _lines = ParseLines(text);
    }

    public static SourceText From(string text, string filePath = "")
        => new(text, filePath);

    public int GetLineIndex(int position)
    {
        int lo = 0, hi = _lines.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (position < _lines[mid].Start)
                hi = mid - 1;
            else if (mid + 1 < _lines.Length && position >= _lines[mid + 1].Start)
                lo = mid + 1;
            else
                return mid;
        }
        return 0;
    }

    public string ToString(TextSpan span) => _text.Substring(span.Start, span.Length);
    public override string ToString() => _text;

    public SourceText WithChanges(TextChange change)
    {
        var newText = string.Concat(
            _text.AsSpan(0, change.Span.Start),
            change.NewText,
            _text.AsSpan(change.Span.End));
        return new SourceText(newText, FilePath);
    }

    private static TextLine[] ParseLines(string text)
    {
        var lines = new List<TextLine>();
        int lineStart = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int lineLength = i - lineStart;
                lines.Add(new TextLine(lineStart, lineLength, lineLength + 1));
                lineStart = i + 1;
            }
            else if (text[i] == '\r')
            {
                int lineLength = i - lineStart;
                int lineBreakWidth = (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
                lines.Add(new TextLine(lineStart, lineLength, lineLength + lineBreakWidth));
                if (lineBreakWidth == 2) i++;
                lineStart = i + 1;
            }
        }

        lines.Add(new TextLine(lineStart, text.Length - lineStart, text.Length - lineStart));
        return lines.ToArray();
    }
}
