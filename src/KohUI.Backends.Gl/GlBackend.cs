using KohUI;
using KohUI.Theme;
using Silk.NET.GLFW;
using Silk.NET.OpenGL;

namespace KohUI.Backends.Gl;

/// <summary>
/// Cross-platform native backend. GLFW owns the window + input; the
/// GL 3.3 core context is created via GLFW and bound to a
/// <see cref="Silk.NET.OpenGL.GL"/> instance. Drawing goes through
/// a <see cref="QuadBatch"/> (solid and textured rectangles) and
/// <see cref="BitmapFont"/> (an embedded 6×8 ASCII atlas). The full
/// native footprint is a single ~230 KB glfw3 binary plus the AOT exe.
/// </summary>
public sealed class GlBackend<TModel, TMsg>
{
    private readonly Runner<TModel, TMsg> _runner;
    private readonly Win98Theme _theme;
    private readonly string _initialTitle;
    private readonly int _width;
    private readonly int _height;
    private readonly Func<TMsg>? _onTick;
    private readonly Func<string, TMsg?>? _onKeyDown;
    private readonly Func<string, TMsg?>? _onKeyUp;

    /// <param name="onTick">
    /// Optional message factory invoked once per rendered frame. Use
    /// this for MVU apps that need a wall-clock cadence (animations,
    /// emulator step loops) rather than event-only redraws — the runner
    /// is otherwise entirely reactive. Tick messages go through
    /// <see cref="Runner{TModel, TMsg}.Dispatch"/> like any other, so
    /// they interleave with mouse/keyboard events in the update loop.
    /// </param>
    /// <param name="onKeyDown">
    /// Optional key-press hook. Receives a DOM-style key name (e.g.
    /// "ArrowUp", "KeyZ", "Enter"); returns a message to dispatch or
    /// null to let the default handling run (Tab focus, accelerators,
    /// Enter/Space click, TextBox typing). Fires for every Press AND
    /// Repeat action so held keys auto-repeat.
    /// </param>
    /// <param name="onKeyUp">Mirror of <paramref name="onKeyDown"/> for release events.</param>
    public GlBackend(
        Runner<TModel, TMsg> runner,
        Win98Theme? theme = null,
        string title = "KohUI",
        int width = 0,
        int height = 0,
        Func<TMsg>? onTick = null,
        Func<string, TMsg?>? onKeyDown = null,
        Func<string, TMsg?>? onKeyUp = null)
    {
        _runner = runner;
        _theme = theme ?? Win98Theme.Default;
        _initialTitle = title;
        _width = width;
        _height = height;
        _onTick = onTick;
        _onKeyDown = onKeyDown;
        _onKeyUp = onKeyUp;
    }

    public unsafe void Run()
    {
        var glfw = Glfw.GetApi();
        if (!glfw.Init())
            throw new InvalidOperationException("glfwInit failed");

        try
        {
            glfw.WindowHint(WindowHintInt.ContextVersionMajor, 3);
            glfw.WindowHint(WindowHintInt.ContextVersionMinor, 3);
            glfw.WindowHint(WindowHintOpenGlProfile.OpenGlProfile, OpenGlProfile.Core);
            glfw.WindowHint(WindowHintBool.OpenGLForwardCompat, true);
            glfw.WindowHint(WindowHintBool.DoubleBuffer, true);
            glfw.WindowHint(WindowHintBool.Resizable, true);

            var (initialW, initialH, title) = ResolveInitialWindowSpec();
            var window = glfw.CreateWindow(initialW, initialH, title, null, null);
            if (window is null)
                throw new InvalidOperationException("glfwCreateWindow failed");

            try
            {
                using var gl = new GlContext(glfw, window);
                RunLoop(glfw, window, gl);
            }
            finally
            {
                glfw.DestroyWindow(window);
            }
        }
        finally
        {
            glfw.Terminate();
        }
    }

    private (int W, int H, string Title) ResolveInitialWindowSpec()
    {
        int w = _width, h = _height;
        string title = _initialTitle;

        var tree = _runner.CurrentRender;
        if (tree is not null)
        {
            if (tree.Props.TryGetValue("title", out var tv) && tv is string tstr) title = tstr;
            if (w <= 0 && tree.Type == "Window" && tree.Props.TryGetValue("width",  out var wv) && wv is int wi && wi > 0) w = wi;
            if (h <= 0 && tree.Type == "Window" && tree.Props.TryGetValue("height", out var hv) && hv is int hi && hi > 0) h = hi;

            if (w <= 0 || h <= 0)
            {
                var measured = MeasureOnce(tree);
                if (w <= 0) w = measured.W;
                if (h <= 0) h = measured.H;
            }
        }

        if (w <= 0) w = 640;
        if (h <= 0) h = 480;
        return (w, h, title);
    }

    /// <summary>
    /// Measure the root without needing a live GL context. The font's
    /// glyph dimensions are constants; we compute label / button sizes
    /// from those + the theme's padding + min-sizes, matching what the
    /// Painter will lay down later.
    /// </summary>
    private (int W, int H) MeasureOnce(RenderNode root)
    {
        var layouter = new Layouter(_theme);
        var node = layouter.Layout(root, 4096, 4096);
        return (node.Bounds.W, node.Bounds.H);
    }

    private unsafe void RunLoop(Glfw glfw, WindowHandle* window, GlContext gl)
    {
        using var batch = new QuadBatch(gl.Gl);
        using var font  = new BitmapFont(gl.Gl);
        using var painter = new Painter(_theme, batch, font, gl.Gl);
        var layouter = new Layouter(_theme);

        string lastTitle = "";
        string? pressedPath = null;
        string? focusPath = null;
        LayoutNode? lastLayout = null;
        double mouseX = 0, mouseY = 0;
        bool closeRequested = false;
        bool escapePressed = false;

        // GLFW input comes in via callbacks. Capture them with closures
        // that flip state the loop body then consumes.
        glfw.SetCursorPosCallback(window, (_, x, y) => { mouseX = x; mouseY = y; });
        glfw.SetMouseButtonCallback(window, (_, btn, action, _) =>
        {
            if (btn != MouseButton.Left) return;
            int ix = (int)mouseX, iy = (int)mouseY;
            if (action == InputAction.Press)
            {
                pressedPath = FindClickTarget(lastLayout, ix, iy);
                if (pressedPath is not null) focusPath = pressedPath;
            }
            else if (action == InputAction.Release)
            {
                if (pressedPath is not null
                    && FindClickTarget(lastLayout, ix, iy) == pressedPath)
                {
                    TryDispatchClick(ix, iy, lastLayout);
                }
                pressedPath = null;
            }
        });
        glfw.SetKeyCallback(window, (_, key, _, action, mods) =>
        {
            string name = KeyName(key);
            if (action == InputAction.Release)
            {
                if (_onKeyUp is not null && _onKeyUp(name) is TMsg msgUp) _runner.Dispatch(msgUp);
                return;
            }
            if (action != InputAction.Press && action != InputAction.Repeat) return;

            // App hook gets first dibs. Returning a message consumes the
            // key — no focus cycling, no click dispatch. Return null to
            // pass through to default handling.
            if (_onKeyDown is not null && _onKeyDown(name) is TMsg msg)
            {
                _runner.Dispatch(msg);
                return;
            }

            if (key == Keys.Escape) { escapePressed = true; return; }
            HandleKeyDown(key, mods, lastLayout, ref focusPath);
        });
        // Unicode-typed characters arrive here (GLFW's equivalent of
        // WM_CHAR). Only printable ASCII lands in our 6×8 atlas; the
        // TextBox append path filters to 32..126 before dispatching.
        glfw.SetCharCallback(window, (_, codepoint) =>
        {
            HandleCharInput(codepoint, lastLayout, focusPath);
        });
        glfw.SetWindowCloseCallback(window, _ => { closeRequested = true; });

        while (!glfw.WindowShouldClose(window))
        {
            glfw.PollEvents();
            if (_onTick is not null) _runner.Dispatch(_onTick());

            if (closeRequested)
            {
                closeRequested = false;
                if (!TryDispatchClose(lastLayout))
                {
                    glfw.SetWindowShouldClose(window, true);
                    break;
                }
            }
            if (escapePressed)
            {
                escapePressed = false;
                TryDispatchClose(lastLayout);
            }

            int w, h;
            glfw.GetFramebufferSize(window, out w, out h);
            if (w <= 0 || h <= 0) { continue; }

            var tree = _runner.CurrentRender;
            if (tree is null) continue;

            SyncWindowTitle(glfw, window, tree, ref lastTitle);
            var layout = layouter.Layout(tree, w, h);
            lastLayout = layout;

            if (focusPath is not null && FindByPath(layout, focusPath) is null) focusPath = null;
            if (pressedPath is not null && FindByPath(layout, pressedPath) is null) pressedPath = null;
            if (focusPath is null)
            {
                foreach (var p in EnumerateFocusable(layout)) { focusPath = p; break; }
            }

            gl.Gl.ClearColor(_theme.Background.R / 255f, _theme.Background.G / 255f, _theme.Background.B / 255f, 1f);
            gl.Gl.Clear(ClearBufferMask.ColorBufferBit);
            batch.BeginFrame(w, h);
            // Caret blink: ~530 ms on / 530 ms off (Windows default).
            bool caretOn = (Environment.TickCount64 % 1060) < 530;
            painter.Paint(layout, pressedPath, focusPath, caretOn);
            batch.Flush();
            gl.SwapBuffers();
        }
    }

    private void TryDispatchClick(int x, int y, LayoutNode? lastLayout)
    {
        if (lastLayout is null) return;
        var hit = HitTest.Find(lastLayout, x, y);
        if (hit is null) return;
        if (hit.Source.Props.TryGetValue("onClick", out var v) && v is Delegate d)
            InvokeHandler(d, hit.Path, "click");
    }

    private bool TryDispatchClose(LayoutNode? lastLayout)
    {
        var root = lastLayout?.Source;
        if (root is null || root.Type != "Window") return false;
        if (!root.Props.TryGetValue("onClose", out var v) || v is not Delegate d) return false;
        InvokeHandler(d, "", "close");
        return true;
    }

    private void InvokeHandler(Delegate d, string path, string kind)
    {
        try
        {
            if (d.DynamicInvoke() is TMsg msg)
                _runner.Dispatch(msg);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[kohui-gl] {kind} handler threw at '{path}': {ex.Message}");
        }
    }

    private static string? FindClickTarget(LayoutNode? lastLayout, int x, int y)
        => lastLayout is null ? null : HitTest.Find(lastLayout, x, y)?.Path;

    private static LayoutNode? FindByPath(LayoutNode root, string path)
        => Focus.FindByPath(root, path);

    private static IEnumerable<string> EnumerateFocusable(LayoutNode root)
    {
        foreach (var p in Focus.Enumerate(root)) yield return p;
    }

    private static unsafe void SyncWindowTitle(Glfw glfw, WindowHandle* window, RenderNode tree, ref string lastTitle)
    {
        if (tree.Type != "Window") return;
        if (!tree.Props.TryGetValue("title", out var t) || t is not string title) return;
        if (title == lastTitle) return;
        glfw.SetWindowTitle(window, title);
        lastTitle = title;
    }

    private void HandleKeyDown(Keys key, KeyModifiers mods, LayoutNode? layout, ref string? focusPath)
    {
        if (layout is null) return;

        bool altHeld = (mods & KeyModifiers.Alt) != 0;
        if (altHeld && key >= Keys.A && key <= Keys.Z)
        {
            char ch = (char)(key - Keys.A + 'A');
            var path = Focus.ResolveAccelerator(layout, ch);
            if (path is not null)
            {
                focusPath = path;
                InvokeByPath(layout, path, "accelerator");
            }
            return;
        }

        if (key == Keys.Tab)
        {
            var order = Focus.Enumerate(layout);
            bool shift = (mods & KeyModifiers.Shift) != 0;
            focusPath = shift ? Focus.Prev(order, focusPath) : Focus.Next(order, focusPath);
        }
        else if (key == Keys.Backspace)
        {
            // Only meaningful on a focused TextBox; silent on everything
            // else so Backspace doesn't double-fire as a "click".
            if (focusPath is null) return;
            var node = Focus.FindByPath(layout, focusPath);
            if (node is null || node.Source.Type != "TextBox") return;
            if (!node.Source.Props.TryGetValue("onChange", out var v) || v is not Delegate d) return;
            string current = Layouter.GetString(node.Source, "text");
            if (current.Length == 0) return;
            DispatchChange(d, focusPath, current[..^1]);
        }
        else if (key == Keys.Enter || key == Keys.KeypadEnter || key == Keys.Space)
        {
            if (focusPath is not null) InvokeByPath(layout, focusPath, "key");
        }
    }

    private void HandleCharInput(uint codepoint, LayoutNode? layout, string? focusPath)
    {
        if (layout is null || focusPath is null) return;
        if (codepoint < 32 || codepoint > 126) return;       // fits our 6×8 atlas
        var node = Focus.FindByPath(layout, focusPath);
        if (node is null || node.Source.Type != "TextBox") return;
        if (!node.Source.Props.TryGetValue("onChange", out var v) || v is not Delegate d) return;
        string current = Layouter.GetString(node.Source, "text");
        DispatchChange(d, focusPath, current + (char)codepoint);
    }

    private void DispatchChange(Delegate d, string path, string next)
    {
        try
        {
            if (d.DynamicInvoke(next) is TMsg msg)
                _runner.Dispatch(msg);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[kohui-gl] change handler threw at '{path}': {ex.Message}");
        }
    }

    private void InvokeByPath(LayoutNode layout, string path, string kind)
    {
        var node = Focus.FindByPath(layout, path);
        if (node is null) return;
        if (!node.Source.Props.TryGetValue("onClick", out var v) || v is not Delegate d) return;
        InvokeHandler(d, path, kind);
    }

    /// <summary>
    /// Map a GLFW key to a DOM-style key name. The subset covered here
    /// is "keys an app is likely to bind" — letters, digits, arrows,
    /// common punctuation, modifiers, Enter/Space/Esc/Tab/Backspace.
    /// Unmapped keys return an empty string rather than throwing; app
    /// hooks check by value so an unknown key is harmless.
    /// </summary>
    private static string KeyName(Keys key) => key switch
    {
        >= Keys.A and <= Keys.Z                 => "Key" + (char)('A' + (key - Keys.A)),
        >= Keys.Number0 and <= Keys.Number9     => "Digit" + (char)('0' + (key - Keys.Number0)),
        Keys.Up       => "ArrowUp",
        Keys.Down     => "ArrowDown",
        Keys.Left     => "ArrowLeft",
        Keys.Right    => "ArrowRight",
        Keys.Enter    => "Enter",
        Keys.KeypadEnter => "Enter",
        Keys.Space    => "Space",
        Keys.Escape   => "Escape",
        Keys.Tab      => "Tab",
        Keys.Backspace => "Backspace",
        Keys.Delete   => "Delete",
        Keys.Home     => "Home",
        Keys.End      => "End",
        Keys.PageUp   => "PageUp",
        Keys.PageDown => "PageDown",
        Keys.ShiftLeft     => "ShiftLeft",
        Keys.ShiftRight    => "ShiftRight",
        Keys.ControlLeft   => "ControlLeft",
        Keys.ControlRight  => "ControlRight",
        Keys.AltLeft       => "AltLeft",
        Keys.AltRight      => "AltRight",
        Keys.Minus    => "Minus",
        Keys.Equal    => "Equal",
        Keys.Comma    => "Comma",
        Keys.Period   => "Period",
        Keys.Slash    => "Slash",
        Keys.Semicolon => "Semicolon",
        Keys.Apostrophe => "Quote",
        Keys.GraveAccent => "Backquote",
        Keys.LeftBracket  => "BracketLeft",
        Keys.RightBracket => "BracketRight",
        Keys.BackSlash    => "Backslash",
        _ => "",
    };
}
