namespace KohUI.Widgets;

/// <summary>
/// Single-line text entry. The v0.1 shape is the minimum MVU-friendly
/// surface: the caller supplies the current <paramref name="Text"/> value
/// and a <paramref name="OnChange"/> factory that produces the next
/// message from the full new string. Every keystroke dispatches one
/// message — the app's <c>update</c> decides what to do with it.
///
/// <code>
/// new TextBox&lt;Msg&gt;(m.Name, OnChange: v =&gt; new SetName(v))
/// </code>
///
/// <para>
/// DomBackend renders as <c>&lt;input type="text"&gt;</c> (Win98 chrome via
/// CSS). GlBackend paints a sunken white field and runs the caret + key
/// input itself. No caret movement in v0.1 — typing appends, Backspace
/// removes the last char; Home/End/Arrow support comes with the caret
/// model in v0.2.
/// </para>
/// </summary>
public readonly struct TextBox<TMsg>(string Text, Func<string, TMsg>? OnChange = null) : IView<TMsg>
{
    public readonly string Text = Text;
    public readonly Func<string, TMsg>? OnChange = OnChange;

    public RenderNode Render()
    {
        var props = Props.Of(
            ("text", Text),
            ("onChange", OnChange));   // carried for the backend, not serialised
        return RenderNode.Leaf("TextBox", props);
    }
}
