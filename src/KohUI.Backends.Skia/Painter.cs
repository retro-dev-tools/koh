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

    /// <summary>
    /// Paint the tree.
    /// <paramref name="pressedPath"/> is the layout path of the button
    /// currently held down by the mouse (inverted Win98 bevel when set).
    /// <paramref name="focusPath"/> is the focused widget (dashed focus
    /// ring inside the bounds).
    /// </summary>
    public void Paint(SKCanvas canvas, LayoutNode node, string? pressedPath = null, string? focusPath = null)
    {
        Draw(canvas, node, pressedPath, focusPath);
        foreach (var child in node.Children) Paint(canvas, child, pressedPath, focusPath);
    }

    private void Draw(SKCanvas canvas, LayoutNode node, string? pressedPath, string? focusPath)
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
                           enabled: node.Source.Props.TryGetValue("enabled", out var en) is false || en is not false,
                           pressed: pressedPath is not null && pressedPath == node.Path,
                           focused: focusPath is not null && focusPath == node.Path);
                break;

            case "MenuItem":
                DrawMenuItem(canvas, Layouter.GetString(node.Source, "text"), r,
                             focused: focusPath is not null && focusPath == node.Path);
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

    private void DrawButton(SKCanvas canvas, Rect r, string text, bool enabled, bool pressed, bool focused)
    {
        canvas.DrawRect(r.ToSKRect(), _bgPaint);
        if (pressed)
        {
            // Inverted bevel on mouse-down: outer top-left is dark, outer
            // bottom-right is hilite (authentic Win98 "pushed in" look).
            DrawBevel(canvas, r,
                outerTL: _darkShadowPaint, outerBR: _hilitePaint,
                innerTL: _shadowPaint,     innerBR: _bgPaint);
        }
        else
        {
            // Raised button: outer top-left = hilite, outer bottom-right =
            // dark shadow; inner one pixel in inverts for the bg/shadow pair.
            DrawBevel(canvas, r,
                outerTL: _hilitePaint, outerBR: _darkShadowPaint,
                innerTL: _bgPaint,     innerBR: _shadowPaint);
        }

        // Win98 shifts the label 1px down-right while the button is held.
        var textR = pressed ? new Rect(r.X + 1, r.Y + 1, r.W, r.H) : r;
        var paint = enabled ? _textPaint : new SKPaint { Color = Convert(_theme.DisabledText), IsAntialias = true };
        DrawText(canvas, text, textR, paint);

        if (focused) DrawFocusRing(canvas, Inset(r, 4));
    }

    private void DrawMenuItem(SKCanvas canvas, string text, Rect r, bool focused)
    {
        // Focused MenuItem shows the Win98 inverted-selection look: blue
        // fill + white text. Keeps text readable without pixel-fighting
        // the dotted focus ring inside a tight menu-bar slot.
        if (focused)
        {
            using var selFill = new SKPaint { Color = Convert(_theme.TitleBarStart), Style = SKPaintStyle.Fill };
            canvas.DrawRect(r.ToSKRect(), selFill);
        }

        var stripped = Layouter.StripAccelerator(text);
        var textPaint = focused
            ? new SKPaint { Color = Convert(_theme.TitleBarText), IsAntialias = true }
            : _textPaint;
        DrawText(canvas, stripped, r, textPaint);

        // Win98 underlines the accelerator character. We lay it out left-
        // aligned (DrawText's default for non-centred boxes), so the
        // underline's x position = DrawText x + width of prefix.
        int amp = text.IndexOf('&');
        if (amp < 0 || amp >= text.Length - 1) return;
        string prefix = stripped.Substring(0, amp);
        char accel = stripped[amp];
        float prefixW = _font.MeasureText(prefix);
        float accelW = _font.MeasureText(accel.ToString());
        float baselineY = r.Y + (r.H - _font.Spacing) / 2f - _font.Metrics.Ascent;
        float underlineY = baselineY + 1;   // 1 px below the glyph baseline
        float x0 = r.X + 4 + prefixW;
        float x1 = x0 + accelW;
        canvas.DrawRect(new SKRect(x0, underlineY, x1, underlineY + 1), textPaint);

        if (focused && textPaint != _textPaint) textPaint.Dispose();
    }

    private void DrawFocusRing(SKCanvas canvas, Rect r)
    {
        // Classic Win98 focus rect: alternating 1-pixel black dots, drawn
        // just inside the widget. We fake the "dotted line" by painting
        // every other pixel around the perimeter.
        var paint = _textPaint;
        // Top + bottom edges.
        for (int x = r.X; x < r.Right; x += 2)
        {
            canvas.DrawRect(new SKRect(x, r.Y, x + 1, r.Y + 1), paint);
            canvas.DrawRect(new SKRect(x, r.Bottom - 1, x + 1, r.Bottom), paint);
        }
        // Left + right edges (skip the already-drawn corners).
        for (int y = r.Y + 2; y < r.Bottom - 2; y += 2)
        {
            canvas.DrawRect(new SKRect(r.X, y, r.X + 1, y + 1), paint);
            canvas.DrawRect(new SKRect(r.Right - 1, y, r.Right, y + 1), paint);
        }
    }

    private static Rect Inset(Rect r, int by)
        => new(r.X + by, r.Y + by, Math.Max(0, r.W - by * 2), Math.Max(0, r.H - by * 2));

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
