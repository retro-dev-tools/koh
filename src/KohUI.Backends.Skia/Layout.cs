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
/// Two-pass, hand-rolled layout:
///
/// <list type="number">
///   <item><b>Measure</b> every node bottom-up and record its preferred
///         (w, h). Leaf widgets ask the <see cref="SKFont"/> for text
///         metrics; containers sum their children along their axis.</item>
///   <item><b>Arrange</b> every node top-down, assigning concrete
///         (x, y) positions and (for containers) distributing leftover
///         space per a simple stretch rule.</item>
/// </list>
///
/// Deliberately not Flexbox. For a retro UI with ~20 controls the
/// payoff of Yoga's layout algebra is small, the binary cost is
/// noticeable, and reaching for it now makes "swap Yoga in later" a
/// simple drop-in if we hit a case this simple layouter doesn't serve.
/// </summary>
public sealed class Layouter(SKFont font, Win98Theme theme)
{
    private readonly SKFont _font = font;
    private readonly int _padding = theme.Padding;
    private readonly int _gap = theme.Gap;
    private readonly int _bevel = theme.BevelWidth;
    private readonly int _buttonMinW = theme.ButtonMinWidth;
    private readonly int _buttonMinH = theme.ButtonMinHeight;
    private readonly int _checkRadioSize = theme.CheckRadioSize;

    public LayoutNode Layout(RenderNode root, int viewportW, int viewportH)
    {
        var (w, h) = Measure(root);
        // Root fits at (0,0) inside viewport; clamp to viewport to avoid
        // content disappearing off-screen on an unexpectedly-tight window.
        int clampedW = Math.Min(w, viewportW);
        int clampedH = Math.Min(h, viewportH);
        return Arrange(root, "", new Rect(0, 0, clampedW, clampedH));
    }

    // ─── Measure pass ────────────────────────────────────────────────

    private (int W, int H) Measure(RenderNode node)
    {
        return node.Type switch
        {
            "Label"             => MeasureText(GetString(node, "text")),
            "Button"            => MeasureButton(node),
            "CheckBox"          => MeasureGlyphed(node, glyphSize: _checkRadioSize, textGap: 6),
            "RadioButton"       => MeasureGlyphed(node, glyphSize: _checkRadioSize, textGap: 6),
            "MenuItem"          => Pad(MeasureText(StripAccelerator(GetString(node, "text"))), px: 8, py: 2),
            "StatusBarSegment"  => Pad(MeasureText(GetString(node, "text")), px: 4, py: 2),
            "Stack"             => MeasureStack(node),
            "Panel"             => MeasureBorder(node, border: _bevel),
            "MenuBar"           => MeasureStack(node, horizontal: true, pad: 0, gap: 0),
            "StatusBar"         => MeasureStack(node, horizontal: true, pad: 0, gap: 2),
            "Window"            => MeasureBorder(node, border: 0),
            _                   => MeasureBorder(node, border: 0),
        };
    }

    private (int W, int H) MeasureText(string text)
    {
        float width = _font.MeasureText(text);
        float height = _font.Spacing;
        return ((int)MathF.Ceiling(width), (int)MathF.Ceiling(height));
    }

    private (int W, int H) MeasureButton(RenderNode node)
    {
        var (tw, th) = MeasureText(GetString(node, "text"));
        // Horizontal padding (12 px each side) is a 98.css constant; the
        // minimum W / H come from the theme so the DOM and Skia builds
        // stay pixel-consistent.
        return (Math.Max(_buttonMinW, tw + 24), Math.Max(_buttonMinH, th + 6));
    }

    /// <summary>
    /// CheckBox / RadioButton: fixed-size glyph box + gap + text.
    /// Height clamps to the max of glyph and text ascender/descender
    /// so the baseline aligns cleanly against the box.
    /// </summary>
    private (int W, int H) MeasureGlyphed(RenderNode node, int glyphSize, int textGap)
    {
        var (tw, th) = MeasureText(GetString(node, "text"));
        return (glyphSize + textGap + tw, Math.Max(glyphSize, th));
    }

    private (int W, int H) MeasureStack(RenderNode node, bool? horizontal = null, int? pad = null, int? gap = null)
    {
        bool h = horizontal ?? GetString(node, "direction") == "Horizontal";
        int p = pad ?? 0;
        int g = gap ?? _gap;

        int main = 0, cross = 0;
        for (int i = 0; i < node.Children.Length; i++)
        {
            var (cw, ch) = Measure(node.Children[i]);
            if (h)
            {
                if (i > 0) main += g;
                main += cw;
                cross = Math.Max(cross, ch);
            }
            else
            {
                if (i > 0) main += g;
                main += ch;
                cross = Math.Max(cross, cw);
            }
        }
        return h ? (main + p * 2, cross + p * 2) : (cross + p * 2, main + p * 2);
    }

    private (int W, int H) MeasureBorder(RenderNode node, int border)
    {
        // Panel-like: wrap the single child (or first child) with padding.
        int contentW = 0, contentH = 0;
        foreach (var c in node.Children)
        {
            var (cw, ch) = Measure(c);
            contentW = Math.Max(contentW, cw);
            contentH += ch;
        }
        int pad = border + _padding;
        return (contentW + pad * 2, contentH + pad * 2);
    }

    private static (int W, int H) Pad((int W, int H) size, int px, int py)
        => (size.W + px * 2, size.H + py * 2);

    // ─── Arrange pass ────────────────────────────────────────────────

    private LayoutNode Arrange(RenderNode node, string path, Rect bounds)
    {
        var children = node.Type switch
        {
            "Stack"    => ArrangeStack(node, path, bounds),
            "MenuBar"  => ArrangeStack(node, path, bounds, horizontal: true, pad: 0, gap: 0),
            "StatusBar"=> ArrangeStack(node, path, bounds, horizontal: true, pad: 0, gap: 2),
            "Panel"    => ArrangeBorder(node, path, bounds, border: _bevel),
            "Window"   => ArrangeBorder(node, path, bounds, border: 0),
            _          => ArrangeBorder(node, path, bounds, border: 0),
        };
        return new LayoutNode { Source = node, Path = path, Bounds = bounds, Children = children };
    }

    private ImmutableArray<LayoutNode> ArrangeStack(
        RenderNode node, string path, Rect bounds,
        bool? horizontal = null, int? pad = null, int? gap = null)
    {
        bool h = horizontal ?? GetString(node, "direction") == "Horizontal";
        int p = pad ?? 0;
        int g = gap ?? _gap;
        bool stretch = node.Props.TryGetValue("stretch", out var sv) && sv is true;

        // When stretching, divide any main-axis space left over after the
        // gaps among children equally. With N children there are (N-1)
        // gaps; the remaining slack bumps each child's measured main
        // dimension by the same integer amount (last child absorbs any
        // pixel rounding).
        int mainTotal = h ? bounds.W - p * 2 : bounds.H - p * 2;
        int gapsTotal = node.Children.Length > 0 ? (node.Children.Length - 1) * g : 0;
        int measuredMainSum = 0;
        if (stretch)
        {
            for (int i = 0; i < node.Children.Length; i++)
            {
                var (cw, ch) = Measure(node.Children[i]);
                measuredMainSum += h ? cw : ch;
            }
        }
        int slack = stretch ? Math.Max(0, mainTotal - gapsTotal - measuredMainSum) : 0;
        int slackPerChild = node.Children.Length > 0 ? slack / node.Children.Length : 0;
        int slackRemainder = slack - slackPerChild * node.Children.Length;

        var result = ImmutableArray.CreateBuilder<LayoutNode>(node.Children.Length);
        int cursor = h ? bounds.X + p : bounds.Y + p;
        int crossStart = h ? bounds.Y + p : bounds.X + p;
        int crossSize  = h ? bounds.H - p * 2 : bounds.W - p * 2;

        for (int i = 0; i < node.Children.Length; i++)
        {
            var (cw, ch) = Measure(node.Children[i]);
            int bump = slackPerChild + (i == node.Children.Length - 1 ? slackRemainder : 0);
            if (h) cw += bump; else ch += bump;

            Rect childBounds = h
                ? new Rect(cursor, crossStart, cw, crossSize)
                : new Rect(crossStart, cursor, crossSize, ch);
            result.Add(Arrange(node.Children[i], Join(path, i), childBounds));
            cursor += (h ? cw : ch) + g;
        }
        return result.ToImmutable();
    }

    private ImmutableArray<LayoutNode> ArrangeBorder(RenderNode node, string path, Rect bounds, int border)
    {
        int pad = border + _padding;
        var inner = new Rect(bounds.X + pad, bounds.Y + pad, bounds.W - pad * 2, bounds.H - pad * 2);

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
