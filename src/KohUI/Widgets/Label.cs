namespace KohUI.Widgets;

/// <summary>
/// Pure text, no chrome. DomBackend renders as <c>&lt;span&gt;</c>;
/// GlBackend draws it with the embedded 6×8 bitmap font.
/// </summary>
public readonly struct Label<TMsg>(string Text) : IView<TMsg>
{
    public readonly string Text = Text;

    public RenderNode Render()
        => RenderNode.Leaf("Label", Props.Of(("text", Text)));
}
