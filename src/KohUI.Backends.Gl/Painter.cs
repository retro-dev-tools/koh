using KohUI;
using KohUI.Theme;
using Silk.NET.OpenGL;

namespace KohUI.Backends.Gl;

/// <summary>
/// Walks a <see cref="LayoutNode"/> tree and submits drawing calls to a
/// <see cref="QuadBatch"/> (solid rectangles) and <see cref="BitmapFont"/>
/// (text). Bevels are plain 1-pixel rects over the OpenGL vertex stream —
/// the full Win98 look from a single shader.
/// </summary>
internal sealed class Painter : IDisposable
{
    private readonly Win98Theme _theme;
    private readonly QuadBatch _batch;
    private readonly BitmapFont _font;
    private readonly GL _gl;
    // One GL texture per Image node, keyed on layout path. Reused across
    // frames — an emulator Image uploads 160×144 RGBA (~92 KB) per paint,
    // so keeping the texture handle around avoids GenTexture churn.
    private readonly Dictionary<string, ImageTexture> _imageTextures = [];
    private readonly HashSet<string> _seenImagePaths = [];

    private readonly KohColor _bg, _hi, _sh, _dk, _text, _dis, _titleBg, _titleBgEnd, _titleText, _inputBg;

    public Painter(Win98Theme theme, QuadBatch batch, BitmapFont font, GL gl)
    {
        _theme = theme;
        _batch = batch;
        _font = font;
        _gl = gl;
        _bg = theme.Background;
        _hi = theme.BevelHilite;
        _sh = theme.BevelShadow;
        _dk = theme.BevelDarkShadow;
        _text = theme.Text;
        _dis = theme.DisabledText;
        _titleBg = theme.TitleBarStart;
        _titleBgEnd = theme.TitleBarEnd;
        _titleText = theme.TitleBarText;
        _inputBg = theme.InputBackground;
    }

    /// <summary>
    /// Paint the tree. <paramref name="pressedPath"/> is the button path
    /// currently held down (inverted bevel). <paramref name="focusPath"/>
    /// is the focused widget (dotted focus ring on buttons; blue
    /// highlight on menu items). <paramref name="caretOn"/> toggles the
    /// TextBox caret on for this frame (the run loop flips it on a
    /// ~530 ms cadence).
    /// </summary>
    public void Paint(LayoutNode node, string? pressedPath = null, string? focusPath = null, bool caretOn = true)
    {
        _seenImagePaths.Clear();
        PaintNode(node, pressedPath, focusPath, caretOn);
        PruneStaleImageTextures();
    }

    private void PaintNode(LayoutNode node, string? pressedPath, string? focusPath, bool caretOn)
    {
        Draw(node, pressedPath, focusPath, caretOn);
        foreach (var child in node.Children) PaintNode(child, pressedPath, focusPath, caretOn);
    }

    private void Draw(LayoutNode node, string? pressedPath, string? focusPath, bool caretOn)
    {
        var r = node.Bounds;
        bool pressed = pressedPath is not null && pressedPath == node.Path;
        bool focused = focusPath   is not null && focusPath   == node.Path;

        switch (node.Source.Type)
        {
            case "Window":
                Fill(r, _bg);
                DrawBevel(r, outerTL: _hi, outerBR: _dk, innerTL: _bg, innerBR: _sh);
                break;

            case "Stack":
            case "MenuBar":
            case "StatusBar":
                // Container widgets paint nothing on their own — children
                // cover the ground.
                break;

            case "Panel":
                DrawPanel(r, Layouter.GetString(node.Source, "bevel"));
                break;

            case "Label":
                DrawText(Layouter.GetString(node.Source, "text"), r, center: false, _text);
                break;

            case "Button":
                DrawButton(r, Layouter.GetString(node.Source, "text"),
                    enabled: !(node.Source.Props.TryGetValue("enabled", out var en) && en is false),
                    pressed, focused);
                break;

            case "MenuItem":
                DrawMenuItem(Layouter.GetString(node.Source, "text"), r, focused);
                break;

            case "StatusBarSegment":
                DrawStatusSegment(Layouter.GetString(node.Source, "text"), r);
                break;

            case "CheckBox":
                DrawCheckBox(r, Layouter.GetString(node.Source, "text"),
                    isChecked: node.Source.Props.TryGetValue("checked",  out var ck) && ck is true,
                    focused);
                break;

            case "RadioButton":
                DrawRadioButton(r, Layouter.GetString(node.Source, "text"),
                    selected: node.Source.Props.TryGetValue("selected", out var sl) && sl is true,
                    focused);
                break;

            case "TextBox":
                DrawTextBox(r, Layouter.GetString(node.Source, "text"), focused, caretOn);
                break;

            case "Image":
                DrawImage(node);
                break;
        }
    }

    // ─── Panels ──────────────────────────────────────────────────────

    private void DrawPanel(Rect r, string bevel)
    {
        Fill(r, _bg);
        switch (bevel)
        {
            case "Raised":   DrawBevel(r, outerTL: _hi, outerBR: _dk, innerTL: _bg, innerBR: _sh); break;
            case "Chiseled": DrawBevel(r, outerTL: _sh, outerBR: _hi, innerTL: _hi, innerBR: _sh); break;
            case "Sunken":
            default:         DrawBevel(r, outerTL: _sh, outerBR: _hi, innerTL: _dk, innerBR: _bg); break;
        }
    }

    // ─── Buttons ─────────────────────────────────────────────────────

    private void DrawButton(Rect r, string text, bool enabled, bool pressed, bool focused)
    {
        Fill(r, _bg);
        if (pressed)
            DrawBevel(r, outerTL: _dk, outerBR: _hi, innerTL: _sh, innerBR: _bg);
        else
            DrawBevel(r, outerTL: _hi, outerBR: _dk, innerTL: _bg, innerBR: _sh);

        var textR = pressed ? new Rect(r.X + 1, r.Y + 1, r.W, r.H) : r;
        var colour = enabled ? _text : _dis;
        DrawText(text, textR, center: true, colour);

        if (focused) DrawFocusRing(Inset(r, 4));
    }

    // ─── Menu ────────────────────────────────────────────────────────

    private void DrawMenuItem(string text, Rect r, bool focused)
    {
        var textColor = _text;
        if (focused)
        {
            Fill(r, _titleBg);
            textColor = _titleText;
        }
        var stripped = Layouter.StripAccelerator(text);
        DrawText(stripped, r, center: false, textColor);

        int amp = text.IndexOf('&');
        if (amp >= 0 && amp < text.Length - 1)
        {
            // Underline the accelerator glyph (6-pixel-wide block at the
            // right pixel-column offset from the text's start).
            int textX = r.X + 4;
            int textY = r.Y + (r.H - BitmapFont.GlyphH) / 2;
            int underlineY = textY + BitmapFont.GlyphH - 1;
            int xStart = textX + amp * BitmapFont.GlyphW;
            Fill(new Rect(xStart, underlineY, BitmapFont.GlyphW, 1), textColor);
        }
    }

    // ─── Status bar ──────────────────────────────────────────────────

    private void DrawStatusSegment(string text, Rect r)
    {
        Fill(r, _bg);
        DrawBevel(r, outerTL: _sh, outerBR: _hi, innerTL: _bg, innerBR: _bg);
        DrawText(text, r, center: false, _text);
    }

    // ─── Check / Radio ───────────────────────────────────────────────

    private void DrawCheckBox(Rect r, string text, bool isChecked, bool focused)
    {
        int glyphY = r.Y + (r.H - _theme.CheckRadioSize) / 2;
        var glyph = new Rect(r.X, glyphY, _theme.CheckRadioSize, _theme.CheckRadioSize);

        Fill(glyph, new KohColor(0xff, 0xff, 0xff));
        DrawBevel(glyph, outerTL: _sh, outerBR: _hi, innerTL: _dk, innerBR: _bg);

        if (isChecked) DrawCheckGlyph(glyph);

        var textR = new Rect(r.X + _theme.CheckRadioSize + 6, r.Y,
                             r.W - _theme.CheckRadioSize - 6, r.H);
        DrawText(text, textR, center: false, _text);

        if (focused)
        {
            int tw = _font.Measure(text);
            var ringR = new Rect(textR.X - 2, textR.Y + (textR.H - BitmapFont.GlyphH) / 2 - 1,
                                 tw + 4, BitmapFont.GlyphH + 2);
            DrawFocusRing(ringR);
        }
    }

    private void DrawRadioButton(Rect r, string text, bool selected, bool focused)
    {
        int glyphY = r.Y + (r.H - _theme.CheckRadioSize) / 2;
        var glyph = new Rect(r.X, glyphY, _theme.CheckRadioSize, _theme.CheckRadioSize);

        DrawRadioGlyph(glyph, selected);

        var textR = new Rect(r.X + _theme.CheckRadioSize + 6, r.Y,
                             r.W - _theme.CheckRadioSize - 6, r.H);
        DrawText(text, textR, center: false, _text);

        if (focused)
        {
            int tw = _font.Measure(text);
            var ringR = new Rect(textR.X - 2, textR.Y + (textR.H - BitmapFont.GlyphH) / 2 - 1,
                                 tw + 4, BitmapFont.GlyphH + 2);
            DrawFocusRing(ringR);
        }
    }

    // ─── Image ───────────────────────────────────────────────────────

    private unsafe void DrawImage(LayoutNode node)
    {
        if (node.Source.Props.TryGetValue("pixels", out var pv) is false || pv is not byte[] pixels) return;
        int width  = node.Source.Props.TryGetValue("width",  out var wv) && wv is int wi ? wi : 0;
        int height = node.Source.Props.TryGetValue("height", out var hv) && hv is int hi ? hi : 0;
        if (width <= 0 || height <= 0) return;
        if (pixels.Length < width * height * 4) return;

        _seenImagePaths.Add(node.Path);

        if (!_imageTextures.TryGetValue(node.Path, out var tex) || tex.Width != width || tex.Height != height)
        {
            if (_imageTextures.TryGetValue(node.Path, out var old)) _gl.DeleteTexture(old.Handle);

            uint handle = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, handle);
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            fixed (byte* p = pixels)
                _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                    (uint)width, (uint)height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
            // Nearest-neighbor for pixel-perfect retro scaling.
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            tex = new ImageTexture(handle, width, height);
            _imageTextures[node.Path] = tex;
        }
        else
        {
            // Same dimensions → reuse the texture, just push new pixels.
            _gl.BindTexture(TextureTarget.Texture2D, tex.Handle);
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            fixed (byte* p = pixels)
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0,
                    (uint)width, (uint)height,
                    PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        var r = node.Bounds;
        _batch.SetTexture(tex.Handle);
        _batch.TexturedQuad(r.X, r.Y, r.W, r.H, 0f, 0f, 1f, 1f, 255, 255, 255);
    }

    private void PruneStaleImageTextures()
    {
        if (_imageTextures.Count == _seenImagePaths.Count) return;
        List<string>? stale = null;
        foreach (var key in _imageTextures.Keys)
        {
            if (_seenImagePaths.Contains(key)) continue;
            stale ??= [];
            stale.Add(key);
        }
        if (stale is null) return;
        foreach (var key in stale)
        {
            _gl.DeleteTexture(_imageTextures[key].Handle);
            _imageTextures.Remove(key);
        }
    }

    public void Dispose()
    {
        foreach (var tex in _imageTextures.Values) _gl.DeleteTexture(tex.Handle);
        _imageTextures.Clear();
    }

    private readonly record struct ImageTexture(uint Handle, int Width, int Height);

    // ─── TextBox ─────────────────────────────────────────────────────

    private void DrawTextBox(Rect r, string text, bool focused, bool caretOn)
    {
        Fill(r, _inputBg);
        DrawBevel(r, outerTL: _sh, outerBR: _hi, innerTL: _dk, innerBR: _bg);

        // Content sits inside the 2-pixel bevel + 3-pixel padding.
        int contentX = r.X + _theme.BevelWidth + 3;
        int contentY = r.Y + (r.H - BitmapFont.GlyphH) / 2;
        if (!string.IsNullOrEmpty(text))
            _font.DrawString(_batch, text, contentX, contentY, _text.R, _text.G, _text.B);

        if (focused && caretOn)
        {
            int caretX = contentX + _font.Measure(text);
            Fill(new Rect(caretX, contentY, 1, BitmapFont.GlyphH), _text);
        }
    }

    private void DrawCheckGlyph(Rect box)
    {
        // Hand-plotted ✓ at 13 px — readable at this scale without
        // needing a vector check-glyph asset.
        int bx = box.X + 3;
        int by = box.Y + 3;
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
            Fill(new Rect(bx + x, by + y, 1, 1), _text);
    }

    private void DrawRadioGlyph(Rect box, bool selected)
    {
        // 13×13 pixel-mapped circle; cells label which theme colour
        // goes in each pixel (D / S / H / B / W).
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
        var white = new KohColor(0xff, 0xff, 0xff);
        for (int ry = 0; ry < rows.Length; ry++)
        {
            string row = rows[ry];
            for (int cx = 0; cx < row.Length; cx++)
            {
                KohColor? c = row[cx] switch
                {
                    'D' => _dk,
                    'S' => _sh,
                    'H' => _hi,
                    'B' => _bg,
                    'W' => white,
                    _   => null,
                };
                if (c is { } col) Fill(new Rect(box.X + cx, box.Y + ry, 1, 1), col);
            }
        }
        if (selected) Fill(new Rect(box.X + 5, box.Y + 5, 3, 3), _text);
    }

    // ─── Primitives ──────────────────────────────────────────────────

    private void Fill(Rect r, KohColor c)
        => _batch.FillRect(r.X, r.Y, r.W, r.H, c.R, c.G, c.B);

    /// <summary>
    /// Classic Win98 2-pixel bevel: outer top+left, outer bottom+right,
    /// inner top+left (1 px inward), inner bottom+right. Four 1-pixel
    /// rectangles per side, 8 fills total.
    /// </summary>
    private void DrawBevel(Rect r, KohColor outerTL, KohColor outerBR, KohColor innerTL, KohColor innerBR)
    {
        // Outer ring
        Fill(new Rect(r.X,           r.Y,           r.W,     1),           outerTL);   // top
        Fill(new Rect(r.X,           r.Y,           1,       r.H),         outerTL);   // left
        Fill(new Rect(r.X,           r.Bottom - 1,  r.W,     1),           outerBR);   // bottom
        Fill(new Rect(r.Right - 1,   r.Y,           1,       r.H),         outerBR);   // right
        // Inner ring
        Fill(new Rect(r.X + 1,       r.Y + 1,       r.W - 2, 1),           innerTL);
        Fill(new Rect(r.X + 1,       r.Y + 1,       1,       r.H - 2),     innerTL);
        Fill(new Rect(r.X + 1,       r.Bottom - 2,  r.W - 2, 1),           innerBR);
        Fill(new Rect(r.Right - 2,   r.Y + 1,       1,       r.H - 2),     innerBR);
    }

    private void DrawText(string text, Rect r, bool center, KohColor colour)
    {
        if (string.IsNullOrEmpty(text)) return;
        int tw = _font.Measure(text);
        int x = center ? r.X + (r.W - tw) / 2 : r.X + 4;
        int y = r.Y + (r.H - BitmapFont.GlyphH) / 2;
        _font.DrawString(_batch, text, x, y, colour.R, colour.G, colour.B);
    }

    private void DrawFocusRing(Rect r)
    {
        // 1-pixel dotted perimeter inset `by` pixels from the widget edge.
        for (int x = r.X; x < r.Right; x += 2)
        {
            Fill(new Rect(x, r.Y, 1, 1), _text);
            Fill(new Rect(x, r.Bottom - 1, 1, 1), _text);
        }
        for (int y = r.Y + 2; y < r.Bottom - 2; y += 2)
        {
            Fill(new Rect(r.X, y, 1, 1), _text);
            Fill(new Rect(r.Right - 1, y, 1, 1), _text);
        }
    }

    private static Rect Inset(Rect r, int by)
        => new(r.X + by, r.Y + by, Math.Max(0, r.W - by * 2), Math.Max(0, r.H - by * 2));
}
