namespace Koh.Core.Syntax;

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
    public bool Contains(int position) => position >= Start && position < End;
    public bool OverlapsWith(TextSpan other) => Start < other.End && other.Start < End;
}
