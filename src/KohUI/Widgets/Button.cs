namespace KohUI.Widgets;

/// <summary>
/// A push button. The caller supplies a <c>Func&lt;TMsg&gt;</c> that the
/// runner invokes when the user clicks — views stay pure, the message
/// factory is the only imperative edge.
///
/// <para>
/// The <c>onClick</c> handler is carried as a prop so the reconciler
/// can diff "clickable vs not clickable" as a prop change, but the
/// actual delegate isn't serialised. Backends register a per-node
/// click callback indexed by the node path and route DOM events to it.
/// </para>
/// </summary>
public readonly struct Button<TMsg>(string Text, Func<TMsg>? OnClick = null, bool Enabled = true) : IView<TMsg>
{
    public readonly string Text = Text;
    public readonly Func<TMsg>? OnClick = OnClick;
    public readonly bool Enabled = Enabled;

    public RenderNode Render()
    {
        var props = Props.Of(
            ("text", Text),
            ("enabled", Enabled),
            ("onClick", OnClick));   // carried for the backend, not serialised
        return RenderNode.Leaf("Button", props);
    }
}
