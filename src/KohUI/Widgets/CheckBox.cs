namespace KohUI.Widgets;

/// <summary>
/// A two-state toggle: sunken 13×13 box beside a label. When the user
/// clicks the label or the box, <paramref name="OnToggle"/> is invoked
/// with the <em>new</em> intended value so the app's <c>update</c> can
/// flip the model field without having to know the current <c>Checked</c>
/// state:
///
/// <code>
/// new CheckBox&lt;Msg&gt;("Enabled", m.Enabled, OnToggle: v =&gt; new SetEnabled(v))
/// </code>
///
/// Both backends render the same layout; GlBackend paints the sunken
/// box and ✓ glyph, DomBackend emits <c>&lt;input type="checkbox"&gt;</c>
/// styled to match.
/// </summary>
public readonly struct CheckBox<TMsg>(string Text, bool Checked, Func<bool, TMsg>? OnToggle = null) : IView<TMsg>
{
    public readonly string Text = Text;
    public readonly bool Checked = Checked;
    public readonly Func<bool, TMsg>? OnToggle = OnToggle;

    public RenderNode Render()
    {
        // Local copies — C# disallows lambdas inside a struct capturing
        // instance members of `this`, since `this` is a value (and on a
        // readonly struct there's no reliable location to capture). Copy
        // to locals once and let the closure own those.
        var onToggle = OnToggle;
        bool current = Checked;
        var onClick = onToggle is null
            ? null
            : (Func<TMsg>)(() => onToggle(!current));

        var props = Props.Of(
            ("text", Text),
            ("checked", Checked),
            // Canonicalise onToggle as a 0-arg factory that captures the
            // "next" value, so the backend-side invocation path is the
            // same shape as every other onClick-style handler.
            ("onClick", onClick));
        return RenderNode.Leaf("CheckBox", props);
    }
}
