using System.Collections.Immutable;

namespace KohUI.Widgets;

/// <summary>
/// Fixed-viewport vertical scroll container. Carries a single child
/// that may be taller than the viewport; the painter clips rendering
/// to the viewport bounds, and the layouter offsets the child by
/// <paramref name="ScrollY"/> so the visible slice corresponds to
/// <c>[ScrollY, ScrollY + ViewportHeight)</c> of the child's own
/// coordinate space.
///
/// <para>
/// Scrolling itself is the caller's responsibility — the widget is
/// stateless in v1, so <paramref name="ScrollY"/> flows in from the
/// model. Apps dispatch their own <c>ScrollBy</c> messages on key /
/// wheel input and clamp the result against the measured child
/// height (which they can obtain from the layout tree after a
/// render, or estimate from content).
/// </para>
///
/// <para>
/// No scrollbar visualisation yet — keyboard-driven scroll is enough
/// for the debugger views this widget was built for. A future
/// revision can layer a Win98-style thumb on the right edge without
/// changing the layout contract.
/// </para>
/// </summary>
public readonly struct ScrollPanel<TMsg, TChild>(
    TChild Child,
    int ViewportWidth,
    int ViewportHeight,
    int ScrollY = 0)
    : IView<TMsg>
    where TChild : IView<TMsg>
{
    public readonly TChild Child = Child;
    public readonly int ViewportWidth = ViewportWidth;
    public readonly int ViewportHeight = ViewportHeight;
    public readonly int ScrollY = ScrollY;

    public RenderNode Render() => RenderNode.WithChildren(
        "ScrollPanel",
        ImmutableArray.Create(Child.Render()),
        Props.Of(
            ("viewportW", ViewportWidth),
            ("viewportH", ViewportHeight),
            ("scrollY",   ScrollY)));
}
