using KohUI;
using KohUI.Theme;
using SkiaSharp;

namespace KohUI.Backends.Skia;

/// <summary>
/// Walks a <see cref="LayoutNode"/> tree and paints it into an
/// <see cref="SKCanvas"/> using <see cref="Win98Theme"/> as the palette
/// source. One instance owns the <c>SKPaint</c> set so per-frame
/// allocation is zero — paints are mutable (Color changes per call)
/// but always reused.
/// </summary>
public sealed class Painter : IDisposable
{
    private readonly Win98Theme _theme;
    private readonly SKFont _font;
    private readonly SKPaint _bgPaint;
    private readonly SKPaint _textPaint;
    private readonly SKPaint _hilitePaint;
    private readonly SKPaint _shadowPaint;
    private readonly SKPaint _darkShadowPaint;

    public Painter(Win98Theme theme, SKFont font)
    {
        _theme = theme;
        _font = font;
        _bgPaint         = new SKPaint { Color = Convert(theme.Background),     Style = SKPaintStyle.Fill };
        _textPaint       = new SKPaint { Color = Convert(theme.Text),           IsAntialias = true };
        _hilitePaint     = new SKPaint { Color = Convert(theme.BevelHilite),    Style = SKPaintStyle.Fill };
        _shadowPaint     = new SKPaint { Color = Convert(theme.BevelShadow),    Style = SKPaintStyle.Fill };
        _darkShadowPaint = new SKPaint { Color = Convert(theme.BevelDarkShadow),Style = SKPaintStyle.Fill };
    }

    public void Paint(SKCanvas canvas, LayoutNode node)
    {
        Draw(canvas, node);
        foreach (var child in node.Children) Paint(canvas, child);
    }

    private void Draw(SKCanvas canvas, LayoutNode node)
    {
        var r = node.Bounds;
        switch (node.Source.Type)
        {
            case "Window":
                // Option 1 (OS chrome): just fill the client area with the
                // panel background. No title bar drawn in Skia — the OS
                // owns everything outside the client area.
                canvas.DrawRect(r.ToSKRect(), _bgPaint);
                break;

            case "Stack":
            case "MenuBar":
                // Containers — nothing to paint themselves (MenuBar
                // inherits the panel background from its Window ancestor).
                break;

            case "StatusBar":
                // One-line background under bevelled segments. Nothing
                // extra to paint; segments draw their own bevels.
                break;

            case "Panel":
                DrawPanel(canvas, r, Layouter.GetString(node.Source, "bevel"));
                break;

            case "Label":
                DrawText(canvas, Layouter.GetString(node.Source, "text"), r, _textPaint);
                break;

            case "Button":
                DrawButton(canvas, r, Layouter.GetString(node.Source, "text"),
                           enabled: node.Source.Props.TryGetValue("enabled", out var en) is false || en is not false);
                break;

            case "MenuItem":
                DrawText(canvas, Layouter.StripAccelerator(Layouter.GetString(node.Source, "text")), r, _textPaint);
                break;

            case "StatusBarSegment":
                DrawPanel(canvas, r, "Sunken");
                DrawText(canvas, Layouter.GetString(node.Source, "text"), r, _textPaint);
                break;
        }
    }

    // ─── Widget drawing primitives ───────────────────────────────────

    private void DrawPanel(SKCanvas canvas, Rect r, string bevel)
    {
        canvas.DrawRect(r.ToSKRect(), _bgPaint);
        switch (bevel)
        {
            case "Sunken":  DrawBevel(canvas, r, outerTL: _shadowPaint, outerBR: _hilitePaint, innerTL: _darkShadowPaint, innerBR: _bgPaint); break;
            case "Raised":  DrawBevel(canvas, r, outerTL: _hilitePaint, outerBR: _darkShadowPaint, innerTL: _bgPaint, innerBR: _shadowPaint); break;
            case "Chiseled":DrawBevel(canvas, r, outerTL: _shadowPaint, outerBR: _hilitePaint, innerTL: _hilitePaint, innerBR: _shadowPaint); break;
            default:        DrawBevel(canvas, r, outerTL: _shadowPaint, outerBR: _hilitePaint, innerTL: _darkShadowPaint, innerBR: _bgPaint); break;
        }
    }

    private void DrawButton(SKCanvas canvas, Rect r, string text, bool enabled)
    {
        canvas.DrawRect(r.ToSKRect(), _bgPaint);
        // Raised button: outer top-left = hilite, outer bottom-right = dark
        // shadow; inner one pixel in = bg-on-top-left, shadow-on-bottom-right.
        DrawBevel(canvas, r, outerTL: _hilitePaint, outerBR: _darkShadowPaint, innerTL: _bgPaint, innerBR: _shadowPaint);
        var paint = enabled ? _textPaint : new SKPaint { Color = Convert(_theme.DisabledText), IsAntialias = true };
        DrawText(canvas, text, r, paint);
    }

    /// <summary>
    /// Draws the canonical Win98 2-pixel bevel: outer top+left one colour,
    /// outer bottom+right another, inner top+left a third, inner
    /// bottom+right a fourth. Four horizontal + four vertical 1-px rects.
    /// </summary>
    private static void DrawBevel(SKCanvas canvas, Rect r,
        SKPaint outerTL, SKPaint outerBR, SKPaint innerTL, SKPaint innerBR)
    {
        // Outer ring
        canvas.DrawRect(new SKRect(r.X, r.Y, r.Right, r.Y + 1), outerTL);          // top
        canvas.DrawRect(new SKRect(r.X, r.Y, r.X + 1, r.Bottom), outerTL);         // left
        canvas.DrawRect(new SKRect(r.X, r.Bottom - 1, r.Right, r.Bottom), outerBR);// bottom
        canvas.DrawRect(new SKRect(r.Right - 1, r.Y, r.Right, r.Bottom), outerBR); // right
        // Inner ring (one pixel inwards)
        canvas.DrawRect(new SKRect(r.X + 1, r.Y + 1, r.Right - 1, r.Y + 2), innerTL);
        canvas.DrawRect(new SKRect(r.X + 1, r.Y + 1, r.X + 2, r.Bottom - 1), innerTL);
        canvas.DrawRect(new SKRect(r.X + 1, r.Bottom - 2, r.Right - 1, r.Bottom - 1), innerBR);
        canvas.DrawRect(new SKRect(r.Right - 2, r.Y + 1, r.Right - 1, r.Bottom - 1), innerBR);
    }

    private void DrawText(SKCanvas canvas, string text, Rect r, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Centre the text vertically within the box; horizontally
        // left-anchor at +pad so controls read left-aligned like Win98.
        var metrics = _font.Metrics;
        float baselineY = r.Y + (r.H - _font.Spacing) / 2f - metrics.Ascent;
        float x = r.X + 4;
        if (IsCenteredWidget(r)) x = r.X + (r.W - _font.MeasureText(text)) / 2f;
        canvas.DrawText(text, x, baselineY, _font, paint);
    }

    /// <summary>
    /// Heuristic: buttons are the widget family we want horizontally-
    /// centred text on. They're the only box with W ≥ 75 and short text
    /// in this v0.1 widget set, so measuring by shape suffices until the
    /// painter starts taking an explicit alignment hint.
    /// </summary>
    private static bool IsCenteredWidget(Rect r) => r.W >= 20 && r.H >= 20 && r.H <= 40 && r.W <= 200;

    private static SKColor Convert(KohColor c) => new(c.R, c.G, c.B);

    public void Dispose()
    {
        _bgPaint.Dispose();
        _textPaint.Dispose();
        _hilitePaint.Dispose();
        _shadowPaint.Dispose();
        _darkShadowPaint.Dispose();
    }
}
