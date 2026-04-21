using System.Collections.Immutable;

namespace KohUI.Widgets;

/// <summary>
/// Bottom-of-window status strip with one or more text segments. Each
/// segment gets its own bevelled slot — same look as the Win98 Explorer
/// status bar ("42 objects | 1.23 MB | Online").
/// </summary>
public readonly struct StatusBar<TMsg>(ImmutableArray<string> Segments) : IView<TMsg>
{
    public readonly ImmutableArray<string> Segments = Segments;

    public RenderNode Render()
    {
        var children = ImmutableArray.CreateBuilder<RenderNode>(Segments.Length);
        foreach (var text in Segments)
            children.Add(RenderNode.Leaf("StatusBarSegment", Props.Of(("text", text))));
        // StatusBar is a horizontal stack by construction; see MenuBar
        // for the rationale behind emitting the "direction" prop.
        return RenderNode.WithChildren("StatusBar", children.MoveToImmutable(),
            Props.Of(("direction", "Horizontal")));
    }
}
