using System.Collections.Immutable;

namespace KohUI.Theme;

/// <summary>
/// The canonical Win98 bevel families. All backends (DOM, GL, any
/// future custom renderer) consume this enum instead of open-coding
/// their own bevel notion.
/// </summary>
public enum BevelStyle
{
    None,
    Sunken,     // content panels, status segments, scrollbar troughs
    Raised,     // buttons, toolbar buttons, raised panels
    Chiseled,   // etched group-box separators
}

/// <summary>Horizontal text anchoring inside a widget's content box.</summary>
public enum TextAlignment
{
    Start,
    Center,
    End,
}

/// <summary>
/// Coarse layout classification. Lets the Layouter dispatch on a small
/// set of algorithms instead of open-coding each widget.
/// </summary>
public enum LayoutKind
{
    /// <summary>Takes no children into its layout; measured from its text or nothing.</summary>
    Leaf,
    /// <summary>Wraps a single child with bevel + padding (Window, Panel).</summary>
    Border,
    /// <summary>Arranges children in a row or column (direction from instance prop).</summary>
    Stack,
}

/// <summary>
/// Single-source structural + visual rule for one widget type. Both
/// backends consume the same dictionary:
///
/// <list type="bullet">
///   <item><b>GL Layouter</b> — <see cref="Layout"/>, <see cref="BevelInset"/>,
///         <see cref="PaddingX"/>/<see cref="PaddingY"/>, <see cref="ChildrenGap"/>,
///         <see cref="MinWidth"/>/<see cref="MinHeight"/> drive measure/arrange.</item>
///   <item><b>GL Painter</b> — <see cref="Bevel"/>, <see cref="DrawBackground"/>,
///         <see cref="TextAlign"/> drive the paint pass.</item>
///   <item><b>DomBackend CSS generator</b> — projects the spec into
///         selector blocks, emitting padding, flex-direction, box-shadow
///         bevels, min-sizes, etc. The hand-written <c>98.css</c> and
///         <c>kohui.css</c> become obsolete.</item>
/// </list>
///
/// Widgets with unique paint (<c>CheckBox</c>'s glyph, <c>RadioButton</c>'s
/// pixel-plotted circle, <c>MenuItem</c>'s accelerator underline) still
/// have a small amount of bespoke code in the Painter, but their
/// structural metrics flow through here.
/// </summary>
public sealed record WidgetSpec
{
    public LayoutKind Layout { get; init; } = LayoutKind.Leaf;
    public BevelStyle Bevel { get; init; } = BevelStyle.None;

    /// <summary>Pixel thickness of the outer bevel ring (0 = no bevel).</summary>
    public int BevelInset { get; init; } = 0;

    /// <summary>Horizontal content padding inside the bevel.</summary>
    public int PaddingX { get; init; } = 0;

    /// <summary>Vertical content padding inside the bevel.</summary>
    public int PaddingY { get; init; } = 0;

    /// <summary>Gap between children of a <see cref="LayoutKind.Stack"/> widget.</summary>
    public int ChildrenGap { get; init; } = 0;

    /// <summary>Horizontal text anchoring for widgets that render a label.</summary>
    public TextAlignment TextAlign { get; init; } = TextAlignment.Start;

    /// <summary>If true, paint the theme's Background colour across the widget's bounds before drawing bevels / text.</summary>
    public bool DrawBackground { get; init; }

    /// <summary>Minimum width in pixels (0 = no minimum, measured from content).</summary>
    public int MinWidth { get; init; } = 0;

    /// <summary>Minimum height in pixels.</summary>
    public int MinHeight { get; init; } = 0;

    /// <summary>Total sideways bleed (bevel + padding) on both sides; handy for Layouter arithmetic.</summary>
    public int SideInset => BevelInset + PaddingX;

    /// <summary>Total vertical bleed (bevel + padding) on both sides.</summary>
    public int TopInset => BevelInset + PaddingY;
}

/// <summary>
/// Canonical Win98 spec table. Keyed by the <c>RenderNode.Type</c>
/// string each widget emits. Values depend on the active <see cref="Win98Theme"/>
/// so per-widget sizes reflect theme tweaks.
/// </summary>
public static class WidgetSpecs
{
    /// <summary>Returns the full set of widget specs parameterised by the theme.</summary>
    public static ImmutableDictionary<string, WidgetSpec> ForTheme(Win98Theme t)
    {
        var b = ImmutableDictionary.CreateBuilder<string, WidgetSpec>();

        // ─── Leaf text widgets ───────────────────────────────────────
        b["Label"] = new WidgetSpec();

        b["MenuItem"] = new WidgetSpec
        {
            PaddingX = 8,
            PaddingY = 2,
        };

        b["StatusBarSegment"] = new WidgetSpec
        {
            Bevel = BevelStyle.Sunken,
            BevelInset = 1,
            PaddingX = 4,
            PaddingY = 2,
        };

        // ─── Interactive leaves ──────────────────────────────────────
        b["Button"] = new WidgetSpec
        {
            Bevel = BevelStyle.Raised,
            BevelInset = t.BevelWidth,
            PaddingX = 12,
            PaddingY = 3,
            TextAlign = TextAlignment.Center,
            DrawBackground = true,
            MinWidth = t.ButtonMinWidth,
            MinHeight = t.ButtonMinHeight,
        };

        // CheckBox / RadioButton are "glyph + label" compound widgets
        // with specialised measure+paint; the spec carries only the
        // bits that survive that specialisation (background + focus
        // behaviour live in code).
        b["CheckBox"]    = new WidgetSpec();
        b["RadioButton"] = new WidgetSpec();

        // TextBox is a sunken white field. The painter paints the input
        // background itself (distinct from the theme's panel bg); the
        // spec just captures the structural metrics.
        b["TextBox"] = new WidgetSpec
        {
            Bevel = BevelStyle.Sunken,
            BevelInset = t.BevelWidth,
            PaddingX = 3,
            PaddingY = 3,
            MinWidth = 60,
            MinHeight = t.ButtonMinHeight,
        };

        // Image is measured from its pixel props (width × scale, height
        // × scale) rather than the spec — the spec entry just anchors
        // the dispatch so the layouter recognises the type. Painter
        // uploads and draws it as a textured quad.
        b["Image"] = new WidgetSpec();

        // ColorSwatch is measured from its size prop, same pattern.
        b["ColorSwatch"] = new WidgetSpec();

        // ScrollPanel reports its own viewport size via props; child
        // layout is driven by the Layouter's specialised routine, not
        // this spec. Entry is just a dispatch anchor.
        b["ScrollPanel"] = new WidgetSpec();

        // ─── Containers that wrap one child ──────────────────────────
        b["Window"] = new WidgetSpec
        {
            Layout = LayoutKind.Border,
            Bevel = BevelStyle.Raised,
            BevelInset = t.BevelWidth,
            DrawBackground = true,
            PaddingX = t.Padding,
            PaddingY = t.Padding,
        };

        // Panel's Bevel here is the *default* — the per-instance
        // "bevel" prop (Sunken/Raised/Chiseled) overrides at render
        // time; spec captures the default structural metrics.
        b["Panel"] = new WidgetSpec
        {
            Layout = LayoutKind.Border,
            Bevel = BevelStyle.Sunken,
            BevelInset = t.BevelWidth,
            PaddingX = t.Padding,
            PaddingY = t.Padding,
            DrawBackground = true,
        };

        // ─── Stack-like containers ───────────────────────────────────
        b["Stack"] = new WidgetSpec
        {
            Layout = LayoutKind.Stack,
            ChildrenGap = t.Gap,
        };

        b["MenuBar"] = new WidgetSpec
        {
            Layout = LayoutKind.Stack,
            ChildrenGap = 0,
        };

        b["StatusBar"] = new WidgetSpec
        {
            Layout = LayoutKind.Stack,
            ChildrenGap = 2,
        };

        return b.ToImmutable();
    }
}
