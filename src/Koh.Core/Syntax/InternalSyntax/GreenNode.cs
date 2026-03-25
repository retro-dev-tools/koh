namespace Koh.Core.Syntax.InternalSyntax;

public abstract class GreenNodeBase
{
    public SyntaxKind Kind { get; }
    public abstract int Width { get; }
    public abstract int FullWidth { get; }
    public abstract int ChildCount { get; }
    public abstract GreenNodeBase? GetChild(int index);

    protected GreenNodeBase(SyntaxKind kind) { Kind = kind; }
}

public sealed class GreenNode : GreenNodeBase
{
    private readonly GreenNodeBase[] _children;

    public override int ChildCount => _children.Length;
    public override int Width => FullWidth;
    public override int FullWidth { get; }

    public GreenNode(SyntaxKind kind, GreenNodeBase[] children) : base(kind)
    {
        _children = children;
        FullWidth = children.Sum(c => c.FullWidth);
    }

    public override GreenNodeBase? GetChild(int index)
    {
        if (index < 0 || index >= _children.Length) return null;
        return _children[index];
    }
}
