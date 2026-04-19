using KohUI;
using KohUI.Theme;
using SDL;
using Silk.NET.OpenGL;
using static SDL.SDL3;

namespace KohUI.Backends.Skia;

/// <summary>
/// Cross-platform native backend. SDL3 owns the window + input; the
/// GL 3.3 core context is created via SDL and bound to a
/// <see cref="Silk.NET.OpenGL.GL"/> instance. Drawing goes through
/// a <see cref="QuadBatch"/> (solid and textured rectangles) and
/// <see cref="BitmapFont"/> (an embedded 6×8 ASCII atlas). No Skia in
/// the graph — the 11 MB libSkiaSharp.dll is no longer shipped.
/// </summary>
public sealed class SkiaBackend<TModel, TMsg>
{
    private readonly Runner<TModel, TMsg> _runner;
    private readonly Win98Theme _theme;
    private readonly string _initialTitle;
    private readonly int _width;
    private readonly int _height;

    public SkiaBackend(
        Runner<TModel, TMsg> runner,
        Win98Theme? theme = null,
        string title = "KohUI",
        int width = 0,
        int height = 0)
    {
        _runner = runner;
        _theme = theme ?? Win98Theme.Default;
        _initialTitle = title;
        _width = width;
        _height = height;
    }

    public unsafe void Run()
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
            throw new InvalidOperationException("SDL_Init failed: " + (SDL_GetError() ?? ""));

        try
        {
            var (initialW, initialH, title) = ResolveInitialWindowSpec();

            SDL_Window* window;
            fixed (byte* titleUtf8 = System.Text.Encoding.UTF8.GetBytes(title + "\0"))
                window = SDL_CreateWindow(titleUtf8, initialW, initialH,
                    SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_OPENGL);
            if (window is null)
                throw new InvalidOperationException("SDL_CreateWindow failed: " + (SDL_GetError() ?? ""));

            try
            {
                using var gl = new GlContext(window);
                RunLoop(window, gl);
            }
            finally
            {
                SDL_DestroyWindow(window);
            }
        }
        finally
        {
            SDL_Quit();
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

    private unsafe void RunLoop(SDL_Window* window, GlContext gl)
    {
        using var batch = new QuadBatch(gl.Gl);
        using var font  = new BitmapFont(gl.Gl);
        var painter = new Painter(_theme, batch, font);
        var layouter = new Layouter(_theme);

        string lastTitle = "";
        bool running = true;
        string? pressedPath = null;
        string? focusPath = null;
        LayoutNode? lastLayout = null;
        int mouseX = 0, mouseY = 0;

        var ev = default(SDL_Event);
        while (running)
        {
            while (SDL_PollEvent(&ev))
            {
                switch ((SDL_EventType)ev.type)
                {
                    case SDL_EventType.SDL_EVENT_QUIT:
                        running = false;
                        break;

                    case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                        if (!TryDispatchClose(lastLayout)) running = false;
                        break;

                    case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                        mouseX = (int)ev.motion.x; mouseY = (int)ev.motion.y;
                        break;

                    case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                        if ((byte)ev.button.Button == SDL_BUTTON_LEFT)
                        {
                            mouseX = (int)ev.button.x; mouseY = (int)ev.button.y;
                            pressedPath = FindClickTarget(lastLayout, mouseX, mouseY);
                            if (pressedPath is not null) focusPath = pressedPath;
                        }
                        break;

                    case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                        if ((byte)ev.button.Button == SDL_BUTTON_LEFT)
                        {
                            mouseX = (int)ev.button.x; mouseY = (int)ev.button.y;
                            if (pressedPath is not null
                                && FindClickTarget(lastLayout, mouseX, mouseY) == pressedPath)
                            {
                                TryDispatchClick(mouseX, mouseY, lastLayout);
                            }
                            pressedPath = null;
                        }
                        break;

                    case SDL_EventType.SDL_EVENT_KEY_DOWN:
                        HandleKeyDown(ev.key, lastLayout, ref focusPath);
                        break;
                }
            }

            int w, h;
            SDL_GetWindowSize(window, &w, &h);
            if (w <= 0 || h <= 0) { SDL_Delay(16); continue; }

            var tree = _runner.CurrentRender;
            if (tree is not null)
            {
                SyncWindowTitle(window, tree, ref lastTitle);
                var layout = layouter.Layout(tree, w, h);
                lastLayout = layout;

                if (focusPath is not null && FindByPath(layout, focusPath) is null) focusPath = null;
                if (pressedPath is not null && FindByPath(layout, pressedPath) is null) pressedPath = null;
                if (focusPath is null)
                {
                    foreach (var p in EnumerateFocusable(layout)) { focusPath = p; break; }
                }

                gl.Gl.Clear(ClearBufferMask.ColorBufferBit);
                gl.Gl.ClearColor(_theme.Background.R / 255f, _theme.Background.G / 255f, _theme.Background.B / 255f, 1f);
                batch.BeginFrame(w, h);
                painter.Paint(layout, pressedPath, focusPath);
                batch.Flush();
                gl.SwapBuffers();
            }
            else
            {
                SDL_Delay(16);
            }
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

    private static unsafe void SyncWindowTitle(SDL_Window* window, RenderNode tree, ref string lastTitle)
    {
        if (tree.Type != "Window") return;
        if (!tree.Props.TryGetValue("title", out var t) || t is not string title) return;
        if (title == lastTitle) return;
        fixed (byte* utf8 = System.Text.Encoding.UTF8.GetBytes(title + "\0"))
            SDL_SetWindowTitle(window, utf8);
        lastTitle = title;
    }

    private void HandleKeyDown(SDL_KeyboardEvent key, LayoutNode? layout, ref string? focusPath)
    {
        if (layout is null) return;
        uint keyCode = (uint)key.key;
        uint mod = (uint)key.mod;

        bool altHeld = (mod & (uint)SDL_Keymod.SDL_KMOD_ALT) != 0;
        if (altHeld && keyCode >= SDLK_A && keyCode <= SDLK_Z)
        {
            char ch = (char)(keyCode - SDLK_A + 'A');
            var path = Focus.ResolveAccelerator(layout, ch);
            if (path is not null)
            {
                focusPath = path;
                InvokeByPath(layout, path, "accelerator");
            }
            return;
        }

        if (keyCode == SDLK_TAB)
        {
            var order = Focus.Enumerate(layout);
            bool shift = (mod & (uint)SDL_Keymod.SDL_KMOD_SHIFT) != 0;
            focusPath = shift ? Focus.Prev(order, focusPath) : Focus.Next(order, focusPath);
        }
        else if (keyCode == SDLK_RETURN || keyCode == SDLK_KP_ENTER || keyCode == SDLK_SPACE)
        {
            if (focusPath is not null) InvokeByPath(layout, focusPath, "key");
        }
        else if (keyCode == SDLK_ESCAPE)
        {
            TryDispatchClose(layout);
        }
    }

    private void InvokeByPath(LayoutNode layout, string path, string kind)
    {
        var node = Focus.FindByPath(layout, path);
        if (node is null) return;
        if (!node.Source.Props.TryGetValue("onClick", out var v) || v is not Delegate d) return;
        InvokeHandler(d, path, kind);
    }
}
