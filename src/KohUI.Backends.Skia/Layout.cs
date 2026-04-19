using System.Collections.Immutable;
using KohUI;
using KohUI.Theme;
using SkiaSharp;

namespace KohUI.Backends.Skia;

/// <summary>
/// Rectangular box. Integer pixels — Win98 widgets are pixel-snapped by
/// design, so no float positioning for widget bounds.
/// </summary>
public readonly record struct Rect(int X, int Y, int W, int H)
{
    public int Right  => X + W;
    public int Bottom => Y + H;
    public bool Contains(int px, int py) => px >= X && py >= Y && px < Right && py < Bottom;
    public SKRect ToSKRect() => new(X, Y, X + W, Y + H);
}

/// <summary>
/// Layout tree node mirroring a <see cref="RenderNode"/> with resolved
/// pixel bounds. Built bottom-up: leaves measure their content, containers
/// accumulate and position children. Read top-down by the painter and the
/// hit tester.
/// </summary>
public sealed class LayoutNode
{
    public required RenderNode Source { get; init; }
    public required string Path { get; init; }
    public required Rect Bounds { get; init; }
    public required ImmutableArray<LayoutNode> Children { get; init; }
}

/// <summary>
/// Two-pass, spec-driven layout. Each widget's structural rules
/// (padding, gap, bevel inset, min sizes) come from
/// <see cref="WidgetSpecs.ForTheme"/>, not hardcoded switch statements.
/// The DomBackend's generated CSS reads the same specs, so a numeric
/// change there propagates to both surfaces.
///
/// <list type="number">
///   <item><b>Measure</b> bottom-up: each node reports its preferred
///         (w, h). Leaf widgets ask the font; container widgets sum
///         their children along an axis and wrap with inset + padding.</item>
///   <item><b>Arrange</b> top-down: assigns concrete (x, y) to each
///         node and distributes Stack stretch space where requested.</item>
/// </list>
///
/// A few widgets (CheckBox, RadioButton) have specialised compound
/// layouts that aren't expressible as Border/Stack — for those the
/// measure step falls back to a small hand-written routine. Everything
/// else flows through the spec.
/// </summary>
public sealed class Layouter(SKFont font, Win98Theme theme)
{
    private readonly SKFont _font = font;
    private readonly Win98Theme _theme = theme;
    private readonly ImmutableDictionary<string, WidgetSpec> _specs = WidgetSpecs.ForTheme(theme);

    private WidgetSpec SpecOf(string type) =>
        _specs.TryGetValue(type, out var s) ? s : new WidgetSpec();

    public LayoutNode Layout(RenderNode root, int viewportW, int viewportH)
    {
        var (w, h) = Measure(root);
        int clampedW = Math.Min(w, viewportW);
        int clampedH = Math.Min(h, viewportH);
        return Arrange(root, "", new Rect(0, 0, clampedW, clampedH));
    }

    // ─── Measure pass ────────────────────────────────────────────────

    private (int W, int H) Measure(RenderNode node)
    {
        // Specialised compound widgets (glyph + label). Small and fixed;
        // don't warrant shoehorning into the generic spec layout.
        if (node.Type == "CheckBox" || node.Type == "RadioButton")
            return MeasureGlyphed(node, _theme.CheckRadioSize, textGap: 6);

        var spec = SpecOf(node.Type);
        return spec.Layout switch
        {
            LayoutKind.Leaf   => MeasureLeaf(node, spec),
            LayoutKind.Border => MeasureBorder(node, spec),
            LayoutKind.Stack  => MeasureStack(node, spec),
            _                 => MeasureLeaf(node, spec),
        };
    }

    private (int W, int H) MeasureText(string text)
    {
        float width = _font.MeasureText(text);
        float height = _font.Spacing;
        return ((int)MathF.Ceiling(width), (int)MathF.Ceiling(height));
    }

    private (int W, int H) MeasureLeaf(RenderNode node, WidgetSpec spec)
    {
        // Default: measure from the node's text prop, pad by the spec,
        // clamp to minimum. Widgets without text (none in v0.1) would
        // return (0, 0) pre-clamp, which is fine.
        string raw = GetString(node, "text");
        string shown = node.Type == "MenuItem" ? StripAccelerator(raw) : raw;
        var (tw, th) = MeasureText(shown);

        int w = tw + spec.PaddingX * 2;
        int h = th + spec.PaddingY * 2;
        if (spec.BevelInset > 0) { w += spec.BevelInset * 2; h += spec.BevelInset * 2; }
        return (Math.Max(spec.MinWidth, w), Math.Max(spec.MinHeight, h));
    }

    private (int W, int H) MeasureBorder(RenderNode node, WidgetSpec spec)
    {
        int contentW = 0, contentH = 0;
        foreach (var c in node.Children)
        {
            var (cw, ch) = Measure(c);
            contentW = Math.Max(contentW, cw);
            contentH += ch;
        }
        int inset = spec.BevelInset + spec.PaddingX; // PaddingY for vertical is symmetric below
        int insetY = spec.BevelInset + spec.PaddingY;
        int w = contentW + inset * 2;
        int h = contentH + insetY * 2;
        return (Math.Max(spec.MinWidth, w), Math.Max(spec.MinHeight, h));
    }

    private (int W, int H) MeasureStack(RenderNode node, WidgetSpec spec)
    {
        bool h = GetString(node, "direction") == "Horizontal";
        int main = 0, cross = 0;
        for (int i = 0; i < node.Children.Length; i++)
        {
            var (cw, ch) = Measure(node.Children[i]);
            if (h)
            {
                if (i > 0) main += spec.ChildrenGap;
                main += cw;
                cross = Math.Max(cross, ch);
            }
            else
            {
                if (i > 0) main += spec.ChildrenGap;
                main += ch;
                cross = Math.Max(cross, cw);
            }
        }
        int w = h ? main : cross;
        int hh = h ? cross : main;
        // Stacks don't bevel themselves by default, but if a specific
        // stack subclass ever did, the inset is already expressed here.
        if (spec.BevelInset > 0) { w += spec.BevelInset * 2; hh += spec.BevelInset * 2; }
        return (w + spec.PaddingX * 2, hh + spec.PaddingY * 2);
    }

    /// <summary>CheckBox / RadioButton: fixed-size glyph + gap + text.</summary>
    private (int W, int H) MeasureGlyphed(RenderNode node, int glyphSize, int textGap)
    {
        var (tw, th) = MeasureText(GetString(node, "text"));
        return (glyphSize + textGap + tw, Math.Max(glyphSize, th));
    }

    // ─── Arrange pass ────────────────────────────────────────────────

    private LayoutNode Arrange(RenderNode node, string path, Rect bounds)
    {
        if (node.Type == "CheckBox" || node.Type == "RadioButton")
            return Leaf(node, path, bounds);

        var spec = SpecOf(node.Type);
        var children = spec.Layout switch
        {
            LayoutKind.Border => ArrangeBorder(node, path, bounds, spec),
            LayoutKind.Stack  => ArrangeStack(node, path, bounds, spec),
            _                 => ImmutableArray<LayoutNode>.Empty,
        };
        return new LayoutNode { Source = node, Path = path, Bounds = bounds, Children = children };
    }

    private LayoutNode Leaf(RenderNode node, string path, Rect bounds)
        => new() { Source = node, Path = path, Bounds = bounds, Children = ImmutableArray<LayoutNode>.Empty };

    private ImmutableArray<LayoutNode> ArrangeStack(RenderNode node, string path, Rect bounds, WidgetSpec spec)
    {
        bool horizontal = GetString(node, "direction") == "Horizontal";
        int gap = spec.ChildrenGap;
        int p = spec.PaddingX;                     // same on both axes for stacks today
        bool stretch = node.Props.TryGetValue("stretch", out var sv) && sv is true;

        int mainTotal = horizontal ? bounds.W - p * 2 : bounds.H - p * 2;
        int gapsTotal = node.Children.Length > 0 ? (node.Children.Length - 1) * gap : 0;
        int measuredMainSum = 0;
        if (stretch)
        {
            for (int i = 0; i < node.Children.Length; i++)
            {
                var (cw, ch) = Measure(node.Children[i]);
                measuredMainSum += horizontal ? cw : ch;
            }
        }
        int slack = stretch ? Math.Max(0, mainTotal - gapsTotal - measuredMainSum) : 0;
        int slackPerChild = node.Children.Length > 0 ? slack / node.Children.Length : 0;
        int slackRemainder = slack - slackPerChild * node.Children.Length;

        var result = ImmutableArray.CreateBuilder<LayoutNode>(node.Children.Length);
        int cursor = horizontal ? bounds.X + p : bounds.Y + p;
        int crossStart = horizontal ? bounds.Y + p : bounds.X + p;
        int crossSize  = horizontal ? bounds.H - p * 2 : bounds.W - p * 2;

        for (int i = 0; i < node.Children.Length; i++)
        {
            var (cw, ch) = Measure(node.Children[i]);
            int bump = slackPerChild + (i == node.Children.Length - 1 ? slackRemainder : 0);
            if (horizontal) cw += bump; else ch += bump;

            Rect childBounds = horizontal
                ? new Rect(cursor, crossStart, cw, crossSize)
                : new Rect(crossStart, cursor, crossSize, ch);
            result.Add(Arrange(node.Children[i], Join(path, i), childBounds));
            cursor += (horizontal ? cw : ch) + gap;
        }
        return result.ToImmutable();
    }

    private ImmutableArray<LayoutNode> ArrangeBorder(RenderNode node, string path, Rect bounds, WidgetSpec spec)
    {
        int padX = spec.BevelInset + spec.PaddingX;
        int padY = spec.BevelInset + spec.PaddingY;
        var inner = new Rect(bounds.X + padX, bounds.Y + padY,
                             bounds.W - padX * 2, bounds.H - padY * 2);

        var result = ImmutableArray.CreateBuilder<LayoutNode>(node.Children.Length);
        int y = inner.Y;
        for (int i = 0; i < node.Children.Length; i++)
        {
            var (_, ch) = Measure(node.Children[i]);
            var childBounds = new Rect(inner.X, y, inner.W, ch);
            result.Add(Arrange(node.Children[i], Join(path, i), childBounds));
            y += ch;
        }
        return result.ToImmutable();
    }

    // ─── Helpers ─────────────────────────────────────────────────────

    internal static string GetString(RenderNode node, string key)
        => node.Props.TryGetValue(key, out var v) && v is string s ? s : "";

    internal static string StripAccelerator(string text)
    {
        int amp = text.IndexOf('&');
        return amp < 0 || amp >= text.Length - 1 ? text : text.Remove(amp, 1);
    }

    private static string Join(string parent, int i) => parent.Length == 0 ? i.ToString() : $"{parent}.{i}";
}
