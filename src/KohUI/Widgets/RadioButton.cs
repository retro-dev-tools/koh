namespace KohUI.Widgets;

/// <summary>
/// Single option in a radio group. The framework doesn't manage group
/// state — callers build a set of RadioButtons from their model, each
/// with its own <c>OnSelect</c> handler, and the app's update flips
/// exactly one flag at a time.
///
/// <code>
/// new RadioButton&lt;Msg&gt;("DMG",      m.Mode == Dmg,      () =&gt; new SetMode(Dmg))
/// new RadioButton&lt;Msg&gt;("Color",    m.Mode == Color,    () =&gt; new SetMode(Color))
/// new RadioButton&lt;Msg&gt;("Advance",  m.Mode == Advance,  () =&gt; new SetMode(Advance))
/// </code>
///
/// Both backends render the same layout; GlBackend paints the sunken
/// 13×13 circle and the center dot, DomBackend uses
/// <c>&lt;input type="radio"&gt;</c>.
/// </summary>
public readonly struct RadioButton<TMsg>(string Text, bool Selected, Func<TMsg>? OnSelect = null) : IView<TMsg>
{
    public readonly string Text = Text;
    public readonly bool Selected = Selected;
    public readonly Func<TMsg>? OnSelect = OnSelect;

    public RenderNode Render()
        => RenderNode.Leaf("RadioButton", Props.Of(
            ("text", Text),
            ("selected", Selected),
            ("onClick", OnSelect)));
}
