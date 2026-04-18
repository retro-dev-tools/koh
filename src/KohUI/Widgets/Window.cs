using System.Collections.Immutable;

namespace KohUI.Widgets;

/// <summary>
/// A Windows-98-style draggable window frame — bevelled outer border,
/// blue title bar with text, close button (minimise / maximise in
/// Phase 2), body content below.
///
/// <para>
/// Position is owned by the backend, not the model. The DOM client
/// implements drag locally via pointer events + CSS transforms; no
/// server round-trip per pixel. If the app cares where the window
/// ended up, Phase 2 will add an <c>onMoved</c> event. Win98 apps
/// almost never care about exact window position in their data model,
/// so keeping it out of the MVU loop is both correct and allocation-
/// free.
/// </para>
/// </summary>
public readonly struct Window<TMsg, TChild>(
    string Title,
    TChild Child,
    int X = 40, int Y = 40, int Width = 320, int Height = 240,
    Func<TMsg>? OnClose = null)
    : IView<TMsg>
    where TChild : IView<TMsg>
{
    public readonly string Title = Title;
    public readonly TChild Child = Child;
    public readonly int X = X;
    public readonly int Y = Y;
    public readonly int Width = Width;
    public readonly int Height = Height;
    public readonly Func<TMsg>? OnClose = OnClose;

    public RenderNode Render()
    {
        var props = Props.Of(
            ("title", Title),
            ("x", X), ("y", Y),
            ("width", Width), ("height", Height),
            ("onClose", OnClose));
        return RenderNode.WithChildren("Window",
            ImmutableArray.Create(Child.Render()),
            props);
    }
}
