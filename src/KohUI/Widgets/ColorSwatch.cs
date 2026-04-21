using KohUI.Theme;

namespace KohUI.Widgets;

/// <summary>
/// Fixed-size solid-colour rectangle with a 1-pixel dark outline.
/// Intended for palette strips, legend swatches, and the like — any
/// place the caller wants to surface a single colour as a visual
/// element. The size is configurable at construction; common choices
/// are 8 × 8 (per-byte compactness) or 12 × 12 (visible hover target).
/// </summary>
public readonly struct ColorSwatch<TMsg>(KohColor Color, int Size = 12) : IView<TMsg>
{
    public readonly KohColor Color = Color;
    public readonly int Size = Size;

    public RenderNode Render() => RenderNode.Leaf("ColorSwatch", Props.Of(
        ("r", (int)Color.R),
        ("g", (int)Color.G),
        ("b", (int)Color.B),
        ("size", Size)));
}
