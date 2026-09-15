namespace Koh.Common;

public readonly record struct TextLine(int Start, int Length, int LengthIncludingLineBreak)
{
    public int End => Start + Length;
}
