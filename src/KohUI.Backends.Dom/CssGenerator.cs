using System.Text;
using KohUI.Theme;

namespace KohUI.Backends.Dom;

/// <summary>
/// Produces the complete DomBackend stylesheet from the
/// <see cref="Win98Theme"/> + the widget-spec table. Replaces what
/// used to be hand-written <c>98.css</c> and <c>kohui.css</c> files.
/// One source of truth: change a value in the theme or in
/// <see cref="WidgetSpecs.ForTheme"/> and the web preview picks it up
/// on the next render.
/// </summary>
internal static class CssGenerator
{
    public static string Build(Win98Theme t)
    {
        var specs = WidgetSpecs.ForTheme(t);
        var sb = new StringBuilder(4096);

        EmitRootVars(sb, t);
        EmitBaseline(sb);

        // Per-widget blocks derive from the spec table. The spec captures
        // structural metrics; a handful of widget types still need a few
        // extra lines (Window title bar chrome, checkbox glyph plumbing,
        // focus rings, pressed button padding shift). Those live below.
        EmitLeaf(sb, ".kohui-label",            specs["Label"],            t);
        EmitButton(sb, specs["Button"], t);
        EmitLeaf(sb, ".kohui-menuitem",         specs["MenuItem"],         t);
        EmitLeaf(sb, ".kohui-statusbarsegment", specs["StatusBarSegment"], t);
        EmitBorder(sb, ".kohui-window",         specs["Window"],           t);
        EmitPanel(sb, t);
        EmitStack(sb, ".kohui-stack",     specs["Stack"],     t);
        EmitStack(sb, ".kohui-menubar",   specs["MenuBar"],   t);
        EmitStack(sb, ".kohui-statusbar", specs["StatusBar"], t);
        EmitCheckAndRadio(sb, t);
        EmitWindowChrome(sb, t);

        return sb.ToString();
    }

    // ─── Root ──────────────────────────────────────────────────────────

    private static void EmitRootVars(StringBuilder sb, Win98Theme t)
    {
        sb.AppendLine(":root {");
        Var(sb, "win98-bg",            t.Background.ToHex());
        Var(sb, "win98-hilite",        t.BevelHilite.ToHex());
        Var(sb, "win98-shadow",        t.BevelShadow.ToHex());
        Var(sb, "win98-dark-shadow",   t.BevelDarkShadow.ToHex());
        Var(sb, "win98-text",          t.Text.ToHex());
        Var(sb, "win98-disabled-text", t.DisabledText.ToHex());
        Var(sb, "win98-title-bg",      t.TitleBarStart.ToHex());
        Var(sb, "win98-title-bg-end",  t.TitleBarEnd.ToHex());
        Var(sb, "win98-title-text",    t.TitleBarText.ToHex());
        Var(sb, "win98-desktop",       t.Desktop.ToHex());
        Var(sb, "win98-ui-font",       $"\"{t.UiFontFamily}\", \"Segoe UI\", sans-serif");
        Var(sb, "win98-ui-font-size",  $"{t.UiFontSize}px");
        sb.AppendLine("}");
    }

    private static void EmitBaseline(StringBuilder sb)
    {
        sb.AppendLine("""

        body {
            margin: 0;
            padding: 0;
            background: var(--win98-desktop);
            font-family: var(--win98-ui-font);
            font-size: var(--win98-ui-font-size);
            color: var(--win98-text);
            min-height: 100vh;
        }
        #kohui-root { position: relative; min-height: 100vh; }
        """);
    }

    // ─── Widget emitters ───────────────────────────────────────────────

    private static void EmitLeaf(StringBuilder sb, string selector, WidgetSpec spec, Win98Theme t)
    {
        sb.Append('\n').Append(selector).AppendLine(" {");
        if (spec.DrawBackground) sb.AppendLine("    background: var(--win98-bg);");
        if (spec.PaddingX > 0 || spec.PaddingY > 0)
            sb.Append("    padding: ").Append(spec.PaddingY).Append("px ").Append(spec.PaddingX).AppendLine("px;");
        if (spec.BevelInset > 0)
            sb.Append("    box-shadow: ").Append(BevelShadow(t, spec.Bevel, spec.BevelInset)).AppendLine(";");
        if (spec.TextAlign != TextAlignment.Start)
            sb.Append("    text-align: ").Append(spec.TextAlign == TextAlignment.Center ? "center" : "right").AppendLine(";");
        sb.AppendLine("    color: var(--win98-text);");
        sb.AppendLine("    user-select: none;");
        sb.AppendLine("}");
    }

    private static void EmitButton(StringBuilder sb, WidgetSpec spec, Win98Theme t)
    {
        sb.AppendLine();
        sb.AppendLine("button, .kohui-button {");
        sb.AppendLine("    box-sizing: content-box;");
        sb.Append("    min-width: ").Append(spec.MinWidth).AppendLine("px;");
        sb.Append("    min-height: ").Append(spec.MinHeight).AppendLine("px;");
        sb.Append("    padding: 0 ").Append(spec.PaddingX).AppendLine("px;");
        sb.AppendLine("    background: var(--win98-bg);");
        sb.AppendLine("    color: var(--win98-text);");
        sb.AppendLine("    font-family: inherit;");
        sb.AppendLine("    font-size: inherit;");
        sb.AppendLine("    text-align: center;");
        sb.AppendLine("    cursor: default;");
        sb.AppendLine("    border: none;");
        sb.Append("    box-shadow: ").Append(BevelShadow(t, BevelStyle.Raised, spec.BevelInset)).AppendLine(";");
        sb.AppendLine("}");
        sb.AppendLine("""

        button:active:not(:disabled), .kohui-button:active:not(:disabled) {
            padding: 1px 11px 0 13px;
        }
        button:focus, .kohui-button:focus {
            outline: 1px dotted var(--win98-text);
            outline-offset: -4px;
        }
        button:disabled, .kohui-button:disabled {
            color: var(--win98-disabled-text);
            text-shadow: 1px 1px 0 var(--win98-hilite);
        }
        """);
        // Pressed bevel inverted.
        sb.Append("button:active:not(:disabled), .kohui-button:active:not(:disabled) { box-shadow: ")
          .Append(BevelShadow(t, BevelStyle.Sunken, spec.BevelInset))
          .AppendLine("; }");
    }

    private static void EmitBorder(StringBuilder sb, string selector, WidgetSpec spec, Win98Theme t)
    {
        sb.Append('\n').Append(selector).AppendLine(" {");
        if (spec.DrawBackground) sb.AppendLine("    background: var(--win98-bg);");
        if (spec.PaddingX > 0 || spec.PaddingY > 0)
            sb.Append("    padding: ").Append(spec.PaddingY).Append("px ").Append(spec.PaddingX).AppendLine("px;");
        sb.AppendLine("    color: var(--win98-text);");
        sb.AppendLine("    user-select: none;");
        sb.AppendLine("}");
    }

    /// <summary>
    /// Panel is per-instance-bevelled: one CSS class per bevel style,
    /// emitted from a single spec + the theme's colour roles.
    /// </summary>
    private static void EmitPanel(StringBuilder sb, Win98Theme t)
    {
        var spec = WidgetSpecs.ForTheme(t)["Panel"];
        foreach (var (suffix, style) in new[]
        {
            ("Sunken",   BevelStyle.Sunken),
            ("Raised",   BevelStyle.Raised),
            ("Chiseled", BevelStyle.Chiseled),
        })
        {
            sb.Append("\n.kohui-panel-").Append(suffix).AppendLine(" {");
            sb.AppendLine("    background: var(--win98-bg);");
            sb.Append("    padding: ").Append(spec.PaddingY).Append("px ").Append(spec.PaddingX).AppendLine("px;");
            sb.Append("    box-shadow: ").Append(BevelShadow(t, style, spec.BevelInset)).AppendLine(";");
            sb.AppendLine("    display: block;");
            sb.AppendLine("    min-height: 1em;");
            sb.AppendLine("}");
        }

        // The .sunken-panel legacy alias from jdan/98.css — kept so apps
        // with their own CSS writing against that class still work.
        sb.Append("\n.sunken-panel { box-shadow: ")
          .Append(BevelShadow(t, BevelStyle.Sunken, spec.BevelInset))
          .AppendLine("; }");
    }

    private static void EmitStack(StringBuilder sb, string selector, WidgetSpec spec, Win98Theme t)
    {
        sb.Append('\n').Append(selector).AppendLine(" {");
        sb.AppendLine("    display: flex;");
        sb.Append("    gap: ").Append(spec.ChildrenGap).AppendLine("px;");
        sb.AppendLine("}");
        sb.Append(selector).AppendLine(".kohui-stack-Vertical   { flex-direction: column; }");
        sb.Append(selector).AppendLine(".kohui-stack-Horizontal { flex-direction: row; align-items: center; }");
        sb.Append(selector).AppendLine(".kohui-stack-stretch > * { flex: 1; }");
    }

    private static void EmitCheckAndRadio(StringBuilder sb, Win98Theme t)
    {
        sb.AppendLine("""

        .kohui-checkbox, .kohui-radiobutton {
            display: inline-flex;
            align-items: center;
            gap: 0;
            cursor: default;
            user-select: none;
        }
        .kohui-checkbox input, .kohui-radiobutton input { margin: 0 4px 0 0; }
        """);
    }

    private static void EmitWindowChrome(StringBuilder sb, Win98Theme t)
    {
        // Title bar + close button. The gradient + X-glyph SVG live here
        // because they're chrome-specific, not spec-derivable from the
        // widget metrics.
        sb.Append("\n.title-bar { background: linear-gradient(90deg, ")
          .Append(t.TitleBarStart.ToHex()).Append(", ")
          .Append(t.TitleBarEnd.ToHex()).AppendLine("); ")
          .AppendLine("    padding: 3px 2px 3px 3px;")
          .AppendLine("    display: flex;")
          .AppendLine("    justify-content: space-between;")
          .AppendLine("    align-items: center;")
          .AppendLine("    cursor: move;")
          .AppendLine("}");

        sb.AppendLine("""
        .title-bar-text {
            font-weight: bold;
            color: var(--win98-title-text);
            margin-left: 2px;
            white-space: nowrap;
            overflow: hidden;
            text-overflow: ellipsis;
        }
        .title-bar-controls { display: flex; }
        .title-bar-controls button {
            min-width: 16px;
            min-height: 14px;
            padding: 0;
            margin-left: 2px;
            background: var(--win98-bg);
            background-image: url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='8' height='7'><path d='M0 0 l7 6 M7 0 l-7 6' stroke='black' stroke-width='1' fill='none'/></svg>");
            background-repeat: no-repeat;
            background-position: center;
        }
        """);

        // Menu-bar items highlight on hover (Win98 convention).
        sb.AppendLine("""

        .kohui-menuitem:hover {
            background: var(--win98-title-bg);
            color: var(--win98-title-text);
        }
        """);

        // window-body wrapper created in JS around the Window's children.
        sb.Append("\n.kohui-window-body { margin: 2px; padding: ")
          .Append(t.Padding).AppendLine("px; }");
    }

    // ─── Bevel synthesis ───────────────────────────────────────────────

    /// <summary>
    /// CSS box-shadow recipe that emulates a Win98 bevel. Two concentric
    /// 1-pixel rings; the colour roles pick which of the four theme
    /// shades fill which side per style.
    /// </summary>
    private static string BevelShadow(Win98Theme t, BevelStyle style, int inset)
    {
        // Widgets with non-default inset could generalise the "1px/2px"
        // literals; 2 is the Win98 standard and matches both backends today.
        string hi = t.BevelHilite.ToHex();
        string sh = t.BevelShadow.ToHex();
        string dk = t.BevelDarkShadow.ToHex();
        string bg = t.Background.ToHex();
        return style switch
        {
            BevelStyle.Raised =>
                $"inset -1px -1px 0 0 {dk}, inset 1px 1px 0 0 {hi}, " +
                $"inset -2px -2px 0 0 {sh}, inset 2px 2px 0 0 {bg}",
            BevelStyle.Sunken =>
                $"inset -1px -1px 0 0 {hi}, inset 1px 1px 0 0 {sh}, " +
                $"inset -2px -2px 0 0 {bg}, inset 2px 2px 0 0 {dk}",
            BevelStyle.Chiseled =>
                $"inset 1px 1px 0 0 {sh}, inset -1px -1px 0 0 {hi}, " +
                $"inset 2px 2px 0 0 {hi}, inset -2px -2px 0 0 {sh}",
            _ => "none",
        };
    }

    private static void Var(StringBuilder sb, string name, string value)
        => sb.Append("    --").Append(name).Append(": ").Append(value).AppendLine(";");
}
