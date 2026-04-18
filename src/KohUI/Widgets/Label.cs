namespace KohUI.Widgets;

/// <summary>
/// Pure text, no chrome. DomBackend renders as <c>&lt;span&gt;</c>;
/// SkiaBackend (later) will draw it flat with <c>SKFont</c>.
/// </summary>
public readonly struct Label<TMsg>(string Text) : IView<TMsg>
{
    public readonly string Text = Text;

    public RenderNode Render()
        => RenderNode.Leaf("Label", Props.Of(("text", Text)));
}
