using System.Collections.Immutable;

namespace KohUI.Widgets;

/// <summary>
/// Orientation for <see cref="Stack{TMsg, TChildren}"/>.
/// </summary>
public enum StackDirection { Vertical, Horizontal }

/// <summary>
/// A container that stacks its children in order. Uses a two-child
/// generic form (monomorphises cleanly) for the common binary case;
/// deeper compositions use nested stacks. For truly dynamic lists use
/// <see cref="ForEach{TMsg, TItem}"/> which boxes deliberately.
///
/// <para>
/// When <see cref="Stretch"/> is set, any leftover space along the
/// main axis is distributed equally among the children — a button
/// row with <c>Stretch=true</c> fills the whole panel, while the
/// default packs children at their measured minimums at the leading
/// edge (the Win98-authentic behaviour).
/// </para>
/// </summary>
public readonly struct Stack<TMsg, TA, TB>(StackDirection Direction, TA First, TB Second, bool Stretch = false) : IView<TMsg>
    where TA : IView<TMsg>
    where TB : IView<TMsg>
{
    public readonly StackDirection Direction = Direction;
    public readonly TA First = First;
    public readonly TB Second = Second;
    public readonly bool Stretch = Stretch;

    public RenderNode Render()
    {
        var children = ImmutableArray.Create(First.Render(), Second.Render());
        var props = Props.Of(
            ("direction", Direction.ToString()),
            ("stretch", Stretch));
        return RenderNode.WithChildren("Stack", children, props);
    }
}

/// <summary>
/// Variable-length children — this is the path that boxes. Use only
/// when the child count depends on the model (lists, tree nodes,
/// repeating rows). For fixed-shape UIs prefer <see cref="Stack{TMsg,
/// TA, TB}"/> composition.
/// </summary>
public readonly struct ForEach<TMsg>(StackDirection Direction, ImmutableArray<IView<TMsg>> Items, bool Stretch = false) : IView<TMsg>
{
    public readonly StackDirection Direction = Direction;
    public readonly ImmutableArray<IView<TMsg>> Items = Items;
    public readonly bool Stretch = Stretch;

    public RenderNode Render()
    {
        var children = ImmutableArray.CreateBuilder<RenderNode>(Items.Length);
        foreach (var item in Items) children.Add(item.Render());
        var props = Props.Of(
            ("direction", Direction.ToString()),
            ("stretch", Stretch));
        return RenderNode.WithChildren("Stack", children.MoveToImmutable(), props);
    }
}
