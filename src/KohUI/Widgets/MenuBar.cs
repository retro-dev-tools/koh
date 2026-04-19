using System.Collections.Immutable;

namespace KohUI.Widgets;

/// <summary>
/// Horizontal menu strip at the top of a window (File / Edit / Help).
/// Items are flat for Phase 1a — dropdowns and submenus come in Phase 2.
/// </summary>
public readonly struct MenuBar<TMsg>(ImmutableArray<MenuItem<TMsg>> Items) : IView<TMsg>
{
    public readonly ImmutableArray<MenuItem<TMsg>> Items = Items;

    public RenderNode Render()
    {
        var children = ImmutableArray.CreateBuilder<RenderNode>(Items.Length);
        foreach (var item in Items) children.Add(item.Render());
        // MenuBar is a horizontal stack by construction; emit the same
        // "direction" prop a Stack would so the shared stack-layout code
        // doesn't need to special-case each container type.
        return RenderNode.WithChildren("MenuBar", children.MoveToImmutable(),
            Props.Of(("direction", "Horizontal")));
    }
}

/// <summary>
/// A single clickable top-level menu item. The <c>&amp;</c>-prefixed
/// character in <paramref name="Text"/> (e.g. <c>"&amp;File"</c>) is
/// rendered underlined, matching the Win98 accelerator convention —
/// actual alt-key triggering ships in Phase 2 alongside keyboard focus.
/// </summary>
public readonly struct MenuItem<TMsg>(string Text, Func<TMsg>? OnClick = null) : IView<TMsg>
{
    public readonly string Text = Text;
    public readonly Func<TMsg>? OnClick = OnClick;

    public RenderNode Render()
        => RenderNode.Leaf("MenuItem", Props.Of(
            ("text", Text),
            ("onClick", OnClick)));
}
