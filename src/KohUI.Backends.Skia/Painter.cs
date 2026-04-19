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

            case "CheckBox":
                DrawCheckBox(canvas, r,
                             Layouter.GetString(node.Source, "text"),
                             isChecked: node.Source.Props.TryGetValue("checked", out var c) && c is true,
                             focused: focusPath is not null && focusPath == node.Path);
                break;

            case "RadioButton":
                DrawRadioButton(canvas, r,
                                Layouter.GetString(node.Source, "text"),
                                selected: node.Source.Props.TryGetValue("selected", out var s) && s is true,
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

    private void DrawCheckBox(SKCanvas canvas, Rect r, string text, bool isChecked, bool focused)
    {
        // Sunken 13×13 box on a white fill (Win98 field background), with
        // a hand-drawn check glyph. Text to the right, vertically centred
        // against the box.
        const int GlyphSize = 13;
        const int TextGap = 6;
        int glyphY = r.Y + (r.H - GlyphSize) / 2;
        var glyph = new Rect(r.X, glyphY, GlyphSize, GlyphSize);

        using var fieldFill = new SKPaint { Color = new SKColor(0xff, 0xff, 0xff), Style = SKPaintStyle.Fill };
        canvas.DrawRect(glyph.ToSKRect(), fieldFill);
        DrawBevel(canvas, glyph,
            outerTL: _shadowPaint,     outerBR: _hilitePaint,
            innerTL: _darkShadowPaint, innerBR: _bgPaint);

        if (isChecked) DrawCheckGlyph(canvas, glyph);

        var textR = new Rect(r.X + GlyphSize + TextGap, r.Y, r.W - GlyphSize - TextGap, r.H);
        DrawText(canvas, text, textR, _textPaint);

        if (focused)
        {
            // Dotted ring hugs the label for keyboard visibility — matches
            // how Win98 renders the focus indicator on checkboxes.
            int tw = (int)_font.MeasureText(text);
            var ringR = new Rect(textR.X - 2, textR.Y + (textR.H - (int)_font.Spacing) / 2 - 1,
                                 tw + 4, (int)_font.Spacing + 2);
            DrawFocusRing(canvas, ringR);
        }
    }

    private void DrawRadioButton(SKCanvas canvas, Rect r, string text, bool selected, bool focused)
    {
        const int GlyphSize = 13;
        const int TextGap = 6;
        int glyphY = r.Y + (r.H - GlyphSize) / 2;
        var glyph = new Rect(r.X, glyphY, GlyphSize, GlyphSize);

        // Outer ring: darkshadow on top-left, hilite on bottom-right.
        // Inner ring: shadow on top-left, bg on bottom-right. White fill
        // inside. No Skia arc primitive at 13 px — hand-plot the pixels to
        // avoid the anti-aliased blur that makes small circles look off at
        // this scale.
        DrawRadioGlyph(canvas, glyph, selected);

        var textR = new Rect(r.X + GlyphSize + TextGap, r.Y, r.W - GlyphSize - TextGap, r.H);
        DrawText(canvas, text, textR, _textPaint);

        if (focused)
        {
            int tw = (int)_font.MeasureText(text);
            var ringR = new Rect(textR.X - 2, textR.Y + (textR.H - (int)_font.Spacing) / 2 - 1,
                                 tw + 4, (int)_font.Spacing + 2);
            DrawFocusRing(canvas, ringR);
        }
    }

    /// <summary>
    /// Eleven-pixel diagonal zig-zag that reads as a Win98 check mark at
    /// the tiny 13-px box size. Hand-laid coordinates; anti-aliased
    /// strokes smear into noise at this density.
    /// </summary>
    private void DrawCheckGlyph(SKCanvas canvas, Rect box)
    {
        int bx = box.X + 3;
        int by = box.Y + 3;
        var paint = _textPaint;
        // Down-right stroke from (0,2) to (3,5), then up-right to (7,1).
        // Each step draws a 2-pixel tall dab to weight the stroke.
        (int x, int y)[] pts =
        [
            (0, 2), (0, 3),
            (1, 3), (1, 4),
            (2, 4), (2, 5),
            (3, 5), (3, 6),
            (3, 4), (4, 4),
            (4, 3), (5, 3),
            (5, 2), (6, 2),
            (6, 1), (7, 1),
            (7, 0),
        ];
        foreach (var (x, y) in pts)
            canvas.DrawRect(new SKRect(bx + x, by + y, bx + x + 1, by + y + 1), paint);
    }

    /// <summary>
    /// Approximation of the Win98 13×13 radio circle: outer bevel drawn
    /// pixel by pixel so the "O" reads cleanly instead of blurring. Fill
    /// is white; dot (when selected) is a 3×3 centred square — Win98 used
    /// a square-ish dot too at this size.
    /// </summary>
    private void DrawRadioGlyph(SKCanvas canvas, Rect box, bool selected)
    {
        // Table of pixel colours along each row of the 13×13 glyph. 'D'
        // = dark shadow, 'S' = shadow, 'H' = hilite, 'B' = bg, 'W' =
        // white fill, '.' = transparent (leave background alone).
        string[] rows =
        [
            "....DDDDD....",
            "..DDSSSSSHH..",
            ".DSSWWWWWBBH.",
            ".DSWWWWWWBBB.",
            "DSWWWWWWWWWBH",
            "DSWWWWWWWWWBH",
            "DSWWWWWWWWWBH",
            "DSWWWWWWWWWBH",
            "DSWWWWWWWWWBH",
            ".DSWWWWWWWBB.",
            ".HSSWWWWWBBB.",
            "..HHSSSSSBB..",
            "....HHHHH....",
        ];

        for (int ry = 0; ry < rows.Length; ry++)
        {
            string row = rows[ry];
            for (int cx = 0; cx < row.Length; cx++)
            {
                SKPaint? p = row[cx] switch
                {
                    'D' => _darkShadowPaint,
                    'S' => _shadowPaint,
                    'H' => _hilitePaint,
                    'B' => _bgPaint,
                    'W' => null, // white handled separately so we only create the paint once
                    _   => null,
                };
                if (p is not null)
                    canvas.DrawRect(new SKRect(box.X + cx, box.Y + ry, box.X + cx + 1, box.Y + ry + 1), p);
            }
        }

        // Fill the interior with white in one rect so the inner shape is
        // solid rather than striped.
        using var whiteFill = new SKPaint { Color = new SKColor(0xff, 0xff, 0xff), Style = SKPaintStyle.Fill };
        canvas.DrawRect(new SKRect(box.X + 3, box.Y + 2, box.X + 10, box.Y + 11), whiteFill);
        canvas.DrawRect(new SKRect(box.X + 2, box.Y + 3, box.X + 11, box.Y + 10), whiteFill);

        if (selected)
        {
            // 3×3 centred dot.
            canvas.DrawRect(new SKRect(box.X + 5, box.Y + 5, box.X + 8, box.Y + 8), _textPaint);
        }
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
