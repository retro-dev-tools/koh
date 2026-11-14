using System.Runtime.InteropServices;
using KohUI;
using KohUI.Theme;
using SDL3;

namespace KohUI.Backends.Sdl3;

public sealed class Sdl3Backend<TModel, TMsg>
{
    private readonly Runner<TModel, TMsg> _runner;
    private readonly Win98Theme _theme;
    private readonly string _initialTitle;
    private readonly int _width;
    private readonly int _height;
    private readonly Func<TMsg>? _onTick;
    private readonly Func<string, TMsg?>? _onKeyDown;
    private readonly Func<string, TMsg?>? _onKeyUp;
    private readonly Dictionary<string, TextEdit> _textEdits = [];

    private sealed class TextEdit(string text, RenderNode? render)
    {
        public readonly object Sync = new();
        public string Text = text;
        public RenderNode? Render = render;
        public int Pending;
    }

    public Sdl3Backend(
        Runner<TModel, TMsg> runner,
        Win98Theme? theme = null,
        string title = "KohUI",
        int width = 0,
        int height = 0,
        Func<TMsg>? onTick = null,
        Func<string, TMsg?>? onKeyDown = null,
        Func<string, TMsg?>? onKeyUp = null
    )
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

    public void Run(CancellationToken cancellationToken = default)
    {
        SDL.SetMainReady();
        if (!SDL.Init(SDL.InitFlags.Video))
            throw new InvalidOperationException(
                "SDL video initialization failed: " + SDL.GetError()
            );
        try
        {
            var (initialW, initialH, title) = ResolveInitialWindowSpec();
            nint window = SDL.CreateWindow(
                title,
                initialW,
                initialH,
                SDL.WindowFlags.Resizable | SDL.WindowFlags.HighPixelDensity
            );
            if (window == 0)
                throw new InvalidOperationException(
                    "SDL window creation failed: " + SDL.GetError()
                );
            try
            {
                using var context = new SdlContext(window);
                if (!SDL.StartTextInput(window))
                    throw new InvalidOperationException("SDL text input failed: " + SDL.GetError());
                try
                {
                    RunLoop(window, context, cancellationToken);
                }
                finally
                {
                    SDL.StopTextInput(window);
                }
            }
            finally
            {
                SDL.DestroyWindow(window);
            }
        }
        finally
        {
            SDL.QuitSubSystem(SDL.InitFlags.Video);
        }
    }

    private (int W, int H, string Title) ResolveInitialWindowSpec()
    {
        int w = _width,
            h = _height;
        string title = _initialTitle;

        var tree = _runner.CurrentRender;
        if (tree is not null)
        {
            if (tree.Props.TryGetValue("title", out var tv) && tv is string tstr)
                title = tstr;
            if (
                w <= 0
                && tree.Type == "Window"
                && tree.Props.TryGetValue("width", out var wv)
                && wv is int wi
                && wi > 0
            )
                w = wi;
            if (
                h <= 0
                && tree.Type == "Window"
                && tree.Props.TryGetValue("height", out var hv)
                && hv is int hi
                && hi > 0
            )
                h = hi;

            if (w <= 0 || h <= 0)
            {
                var measured = MeasureOnce(tree);
                if (w <= 0)
                    w = measured.W;
                if (h <= 0)
                    h = measured.H;
            }
        }

        if (w <= 0)
            w = 640;
        if (h <= 0)
            h = 480;
        return (w, h, title);
    }

    private (int W, int H) MeasureOnce(RenderNode root)
    {
        var layouter = new Layouter(_theme);
        var node = layouter.Layout(root, 4096, 4096);
        return (node.Bounds.W, node.Bounds.H);
    }

    private void RunLoop(nint window, SdlContext context, CancellationToken cancellationToken)
    {
        using var batch = new QuadBatch(context);
        using var font = new BitmapFont(context);
        using var painter = new Painter(_theme, batch, font, context);
        var layouter = new Layouter(_theme);
        string lastTitle = "";
        int lastRequestedWidth = 0,
            lastRequestedHeight = 0;
        string? pressedPath = null,
            focusPath = null;
        LayoutNode? lastLayout = null;
        var heldKeys = new HashSet<SDL.Scancode>();
        uint windowId = SDL.GetWindowID(window);
        bool running = true;
        while (running && !cancellationToken.IsCancellationRequested)
        {
            ulong frameStarted = SDL.GetTicks();
            while (SDL.PollEvent(out var e))
            {
                switch ((SDL.EventType)e.Type)
                {
                    case SDL.EventType.Quit:
                        running = false;
                        break;
                    case SDL.EventType.WindowCloseRequested when e.Window.WindowID == windowId:
                        if (!TryDispatchClose(lastLayout))
                            running = false;
                        break;
                    case SDL.EventType.WindowFocusLost when e.Window.WindowID == windowId:
                        foreach (var key in heldKeys)
                            if (_onKeyUp is not null && _onKeyUp(KeyName(key)) is TMsg up)
                                _runner.Dispatch(up);
                        heldKeys.Clear();
                        pressedPath = null;
                        break;
                    case SDL.EventType.MouseButtonDown
                        when e.Button.WindowID == windowId && e.Button.Button == SDL.ButtonLeft:
                        pressedPath = FindClickTarget(lastLayout, (int)e.Button.X, (int)e.Button.Y);
                        if (pressedPath is not null)
                            focusPath = pressedPath;
                        break;
                    case SDL.EventType.MouseButtonUp
                        when e.Button.WindowID == windowId && e.Button.Button == SDL.ButtonLeft:
                        if (
                            pressedPath is not null
                            && FindClickTarget(lastLayout, (int)e.Button.X, (int)e.Button.Y)
                                == pressedPath
                        )
                            TryDispatchClick((int)e.Button.X, (int)e.Button.Y, lastLayout);
                        pressedPath = null;
                        break;
                    case SDL.EventType.KeyUp when e.Key.WindowID == windowId:
                        heldKeys.Remove(e.Key.Scancode);
                        if (
                            _onKeyUp is not null
                            && _onKeyUp(KeyName(e.Key.Scancode)) is TMsg released
                        )
                            _runner.Dispatch(released);
                        break;
                    case SDL.EventType.KeyDown when e.Key.WindowID == windowId:
                        heldKeys.Add(e.Key.Scancode);
                        if (
                            _onKeyDown is not null
                            && _onKeyDown(KeyName(e.Key.Scancode)) is TMsg message
                        )
                            _runner.Dispatch(message);
                        else if (e.Key.Scancode == SDL.Scancode.Escape)
                            TryDispatchClose(lastLayout);
                        else
                            HandleKeyDown(e.Key.Scancode, e.Key.Mod, lastLayout, ref focusPath);
                        break;
                    case SDL.EventType.TextInput when e.Text.WindowID == windowId:
                        HandleTextInput(
                            Marshal.PtrToStringUTF8(e.Text.Text) ?? "",
                            lastLayout,
                            focusPath
                        );
                        break;
                    case SDL.EventType.RenderDeviceLost:
                    case SDL.EventType.RenderDeviceReset:
                        throw new InvalidOperationException(
                            "SDL renderer device lost: " + SDL.GetError()
                        );
                }
            }
            if (!running || cancellationToken.IsCancellationRequested)
                break;
            if (_onTick is not null)
                _runner.Dispatch(_onTick());
            var tree = _runner.CurrentRender;
            if (tree is null)
            {
                SDL.Delay(1);
                continue;
            }
            SyncWindowTitle(window, tree, ref lastTitle);
            SyncWindowSize(window, tree, ref lastRequestedWidth, ref lastRequestedHeight);
            if (!SDL.GetWindowSize(window, out int w, out int h))
                throw new InvalidOperationException("SDL window size failed: " + SDL.GetError());
            if (w <= 0 || h <= 0 || (SDL.GetWindowFlags(window) & SDL.WindowFlags.Minimized) != 0)
            {
                SDL.Delay(10);
                continue;
            }
            var layout = layouter.Layout(tree, w, h);
            lastLayout = layout;
            foreach (string path in _textEdits.Keys.ToArray())
                if (FindByPath(layout, path)?.Source.Type != "TextBox")
                    _textEdits.Remove(path);
            if (focusPath is not null && FindByPath(layout, focusPath) is null)
                focusPath = null;
            if (pressedPath is not null && FindByPath(layout, pressedPath) is null)
                pressedPath = null;
            if (focusPath is null)
                focusPath = EnumerateFocusable(layout).FirstOrDefault();
            if (
                !context.BeginFrame(
                    w,
                    h,
                    _theme.Background.R,
                    _theme.Background.G,
                    _theme.Background.B
                )
            )
            {
                SDL.Delay(10);
                continue;
            }
            batch.BeginFrame(w, h);
            painter.Paint(layout, pressedPath, focusPath, Environment.TickCount64 % 1060 < 530);
            batch.Flush();
            context.EndFrame();
            ulong elapsed = SDL.GetTicks() - frameStarted;
            if (elapsed < 16)
                SDL.Delay((uint)(16 - elapsed));
        }
    }

    private void TryDispatchClick(int x, int y, LayoutNode? lastLayout)
    {
        if (lastLayout is null)
            return;
        var hit = HitTest.Find(lastLayout, x, y);
        if (hit is null)
            return;
        if (hit.Source.Props.TryGetValue("onClick", out var v) && v is Delegate d)
            InvokeHandler(d, hit.Path, "click");
    }

    private bool TryDispatchClose(LayoutNode? lastLayout)
    {
        var root = lastLayout?.Source;
        if (root is null || root.Type != "Window")
            return false;
        if (!root.Props.TryGetValue("onClose", out var v) || v is not Delegate d)
            return false;
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
            Console.Error.WriteLine($"[kohui-sdl3] {kind} handler threw at '{path}': {ex.Message}");
        }
    }

    private static string? FindClickTarget(LayoutNode? lastLayout, int x, int y) =>
        lastLayout is null ? null : HitTest.Find(lastLayout, x, y)?.Path;

    private static LayoutNode? FindByPath(LayoutNode root, string path) =>
        Focus.FindByPath(root, path);

    private static IEnumerable<string> EnumerateFocusable(LayoutNode root)
    {
        foreach (var p in Focus.Enumerate(root))
            yield return p;
    }

    private static void SyncWindowTitle(nint window, RenderNode tree, ref string lastTitle)
    {
        if (tree.Type != "Window")
            return;
        if (!tree.Props.TryGetValue("title", out var t) || t is not string title)
            return;
        if (title == lastTitle)
            return;
        if (!SDL.SetWindowTitle(window, title))
            throw new InvalidOperationException(SDL.GetError());
        lastTitle = title;
    }

    private static void SyncWindowSize(
        nint window,
        RenderNode tree,
        ref int lastWidth,
        ref int lastHeight
    )
    {
        if (tree.Type != "Window")
            return;
        bool hasW = tree.Props.TryGetValue("width", out var wv) && wv is int ww && ww > 0;
        bool hasH = tree.Props.TryGetValue("height", out var hv) && hv is int hh && hh > 0;
        if (!hasW && !hasH)
            return;
        if (!SDL.GetWindowSize(window, out int curW, out int curH))
            throw new InvalidOperationException(SDL.GetError());
        int desiredW = hasW ? (int)wv! : curW;
        int desiredH = hasH ? (int)hv! : curH;
        if ((!hasW || desiredW == lastWidth) && (!hasH || desiredH == lastHeight))
            return;
        lastWidth = hasW ? desiredW : 0;
        lastHeight = hasH ? desiredH : 0;
        if (desiredW == curW && desiredH == curH)
            return;
        if (!SDL.SetWindowSize(window, desiredW, desiredH))
            throw new InvalidOperationException(SDL.GetError());
    }

    private void HandleKeyDown(
        SDL.Scancode key,
        SDL.Keymod mods,
        LayoutNode? layout,
        ref string? focusPath
    )
    {
        if (layout is null)
            return;

        bool altHeld = (mods & SDL.Keymod.Alt) != 0;
        if (altHeld && key >= SDL.Scancode.A && key <= SDL.Scancode.Z)
        {
            char ch = (char)(key - SDL.Scancode.A + 'A');
            var path = Focus.ResolveAccelerator(layout, ch);
            if (path is not null)
            {
                focusPath = path;
                InvokeByPath(layout, path, "accelerator");
            }
            return;
        }

        if (key == SDL.Scancode.Tab)
        {
            var order = Focus.Enumerate(layout);
            bool shift = (mods & SDL.Keymod.Shift) != 0;
            focusPath = shift ? Focus.Prev(order, focusPath) : Focus.Next(order, focusPath);
        }
        else if (key == SDL.Scancode.Backspace)
        {
            if (focusPath is null)
                return;
            var node = Focus.FindByPath(layout, focusPath);
            if (node is null || node.Source.Type != "TextBox")
                return;
            if (!node.Source.Props.TryGetValue("onChange", out var v) || v is not Delegate d)
                return;
            string current = GetInputText(node);
            if (current.Length == 0)
                return;
            DispatchChange(d, focusPath, current[..^1]);
        }
        else if (
            key == SDL.Scancode.Return
            || key == SDL.Scancode.KpEnter
            || key == SDL.Scancode.Space
        )
        {
            if (focusPath is not null)
                InvokeByPath(layout, focusPath, "key");
        }
    }

    private void HandleTextInput(string text, LayoutNode? layout, string? focusPath)
    {
        if (layout is null || focusPath is null)
            return;
        var node = Focus.FindByPath(layout, focusPath);
        if (node is null || node.Source.Type != "TextBox")
            return;
        if (!node.Source.Props.TryGetValue("onChange", out var v) || v is not Delegate d)
            return;
        string printable = new(text.Where(ch => ch >= 32 && ch <= 126).ToArray());
        if (printable.Length == 0)
            return;
        DispatchChange(d, focusPath, GetInputText(node) + printable);
    }

    private string GetInputText(LayoutNode node)
    {
        if (!_textEdits.TryGetValue(node.Path, out var edit))
        {
            var render = _runner.CurrentRender;
            _textEdits[node.Path] = edit = new TextEdit(InputText(render, node.Path), render);
        }
        lock (edit.Sync)
        {
            if (edit.Pending == 0 && edit.Render != _runner.CurrentRender)
            {
                edit.Render = _runner.CurrentRender;
                edit.Text = InputText(edit.Render, node.Path);
            }
            return edit.Text;
        }
    }

    private static string InputText(RenderNode? node, string path)
    {
        if (node is null)
            return "";
        if (path.Length > 0)
            foreach (string part in path.Split('.'))
            {
                if (!int.TryParse(part, out int index) || (uint)index >= node.Children.Length)
                    return "";
                node = node.Children[index];
            }
        return Layouter.GetString(node, "text");
    }

    private void DispatchChange(Delegate d, string path, string next)
    {
        try
        {
            if (d.DynamicInvoke(next) is TMsg msg)
            {
                var edit = _textEdits[path];
                lock (edit.Sync)
                {
                    edit.Text = next;
                    edit.Pending++;
                }
                _runner.Dispatch(
                    msg,
                    () =>
                    {
                        lock (edit.Sync)
                        {
                            if (--edit.Pending == 0)
                            {
                                edit.Render = _runner.CurrentRender;
                                edit.Text = InputText(edit.Render, path);
                            }
                        }
                    }
                );
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[kohui-sdl3] change handler threw at '{path}': {ex.Message}");
        }
    }

    private void InvokeByPath(LayoutNode layout, string path, string kind)
    {
        var node = Focus.FindByPath(layout, path);
        if (node is null)
            return;
        if (!node.Source.Props.TryGetValue("onClick", out var v) || v is not Delegate d)
            return;
        InvokeHandler(d, path, kind);
    }

    private static string KeyName(SDL.Scancode key) =>
        key switch
        {
            >= SDL.Scancode.A and <= SDL.Scancode.Z => "Key" + (char)('A' + (key - SDL.Scancode.A)),
            >= SDL.Scancode.Alpha1 and <= SDL.Scancode.Alpha9 => "Digit"
                + (char)('1' + (key - SDL.Scancode.Alpha1)),
            SDL.Scancode.Alpha0 => "Digit0",
            >= SDL.Scancode.F1 and <= SDL.Scancode.F12 => "F" + (1 + (key - SDL.Scancode.F1)),
            SDL.Scancode.Up => "ArrowUp",
            SDL.Scancode.Down => "ArrowDown",
            SDL.Scancode.Left => "ArrowLeft",
            SDL.Scancode.Right => "ArrowRight",
            SDL.Scancode.Return => "Enter",
            SDL.Scancode.KpEnter => "Enter",
            SDL.Scancode.Space => "Space",
            SDL.Scancode.Escape => "Escape",
            SDL.Scancode.Tab => "Tab",
            SDL.Scancode.Backspace => "Backspace",
            SDL.Scancode.Delete => "Delete",
            SDL.Scancode.Home => "Home",
            SDL.Scancode.End => "End",
            SDL.Scancode.Pageup => "PageUp",
            SDL.Scancode.Pagedown => "PageDown",
            SDL.Scancode.LShift => "ShiftLeft",
            SDL.Scancode.RShift => "ShiftRight",
            SDL.Scancode.LCtrl => "ControlLeft",
            SDL.Scancode.RCtrl => "ControlRight",
            SDL.Scancode.LAlt => "AltLeft",
            SDL.Scancode.RAlt => "AltRight",
            SDL.Scancode.Minus => "Minus",
            SDL.Scancode.Equals => "Equal",
            SDL.Scancode.Comma => "Comma",
            SDL.Scancode.Period => "Period",
            SDL.Scancode.Slash => "Slash",
            SDL.Scancode.Semicolon => "Semicolon",
            SDL.Scancode.Apostrophe => "Quote",
            SDL.Scancode.Grave => "Backquote",
            SDL.Scancode.Leftbracket => "BracketLeft",
            SDL.Scancode.Rightbracket => "BracketRight",
            SDL.Scancode.Backslash => "Backslash",
            _ => "",
        };
}
