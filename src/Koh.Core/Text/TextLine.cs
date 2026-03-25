namespace Koh.Core.Text;

public readonly record struct TextLine(int Start, int Length, int LengthIncludingLineBreak)
{
    public int End => Start + Length;
}
