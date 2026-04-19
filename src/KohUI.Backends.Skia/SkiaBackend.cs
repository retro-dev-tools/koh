using System.Runtime.InteropServices;
using KohUI;
using KohUI.Theme;
using SDL;
using SkiaSharp;
using static SDL.SDL3;

namespace KohUI.Backends.Skia;

/// <summary>
/// Cross-platform native backend. Opens one SDL3 window with OS-native
/// chrome (option 1 — title bar from the OS, body painted by Skia),
/// runs a three-phase per-frame loop:
///
/// <list type="number">
///   <item><see cref="Layouter"/> walks the current
///         <see cref="Runner{TModel, TMsg}.CurrentRender"/> tree and
///         assigns pixel bounds to every widget.</item>
///   <item><see cref="Painter"/> rasterises the laid-out tree into a
///         software <see cref="SKSurface"/> whose pixels live in a
///         pinned managed byte[].</item>
///   <item>SDL uploads those pixels to a streaming texture and blits to
///         the window.</item>
/// </list>
///
/// Mouse button-up events are resolved against the layout tree via
/// <see cref="HitTest.Find"/>; if the hit node has an <c>onClick</c>
/// delegate prop, it's invoked and the returned message goes through
/// <see cref="Runner{TModel, TMsg}.Dispatch"/>, which triggers
/// re-rendering automatically. The SDL window title syncs with the
/// root <c>Window</c>'s <c>Title</c> prop each tick.
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
        int width = 640,
        int height = 480)
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
            throw new InvalidOperationException("SDL_Init failed: " + SDL_GetError());

        try
        {
            SDL_Window* window;
            fixed (byte* titleUtf8 = System.Text.Encoding.UTF8.GetBytes(_initialTitle + "\0"))
                window = SDL_CreateWindow(titleUtf8, _width, _height, SDL_WindowFlags.SDL_WINDOW_RESIZABLE);
            if (window is null)
                throw new InvalidOperationException("SDL_CreateWindow failed: " + SDL_GetError());

            SDL_Renderer* renderer = SDL_CreateRenderer(window, (byte*)null);
            if (renderer is null)
                throw new InvalidOperationException("SDL_CreateRenderer failed: " + SDL_GetError());

            try
            {
                RunLoop(window, renderer);
            }
            finally
            {
                SDL_DestroyRenderer(renderer);
                SDL_DestroyWindow(window);
            }
        }
        finally
        {
            SDL_Quit();
        }
    }

    private unsafe void RunLoop(SDL_Window* window, SDL_Renderer* renderer)
    {
        using var typeface = SKTypeface.FromFamilyName(_theme.UiFontFamily) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, _theme.UiFontSize + 2f);
        using var painter = new Painter(_theme, font);
        var layouter = new Layouter(font);

        // Pixel buffer + Skia surface sized to match the window's client
        // area. When the user resizes, we reallocate lazily.
        var pixelState = new PixelState();
        string lastTitle = "";

        SDL_Texture* texture = null;

        try
        {
            var ev = default(SDL_Event);
            bool running = true;
            while (running)
            {
                // ─── Event pump ───────────────────────────────────────
                while (SDL_PollEvent(&ev))
                {
                    switch ((SDL_EventType)ev.type)
                    {
                        case SDL_EventType.SDL_EVENT_QUIT:
                            running = false;
                            break;

                        case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                            // SDLButton is an enum wrapper; SDL_BUTTON_LEFT == 1 is the
                            // C-level constant. Cast once to compare without depending
                            // on the enum's runtime-unstable symbolic names.
                            if ((byte)ev.button.Button == SDL_BUTTON_LEFT)
                                TryDispatchClick((int)ev.button.x, (int)ev.button.y, pixelState.LastLayout);
                            break;
                    }
                }

                // ─── Resize check ─────────────────────────────────────
                int w, h;
                SDL_GetWindowSize(window, &w, &h);
                if (w <= 0 || h <= 0) { SDL_Delay(16); continue; }

                if (pixelState.Width != w || pixelState.Height != h || texture is null)
                {
                    pixelState.Resize(w, h);
                    if (texture is not null) SDL_DestroyTexture(texture);
                    texture = SDL_CreateTexture(renderer,
                        // Want: memory order [B][G][R][A] matching Skia's SKColorType.Bgra8888.
                // On little-endian that's packed 0xAARRGGBB == SDL's ARGB8888.
                // (SDL_PIXELFORMAT_BGRA32 is a C macro alias for exactly this on
                // little-endian hosts but the alias doesn't make it through the
                // managed binding, so we spell out the packed form.)
                SDL_PixelFormat.SDL_PIXELFORMAT_ARGB8888,
                        SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
                        w, h);
                    if (texture is null)
                        throw new InvalidOperationException("SDL_CreateTexture failed: " + SDL_GetError());
                }

                // ─── Layout + paint ───────────────────────────────────
                var tree = _runner.CurrentRender;
                if (tree is not null)
                {
                    SyncWindowTitle(window, tree, ref lastTitle);
                    var layout = layouter.Layout(tree, w, h);
                    pixelState.LastLayout = layout;

                    var surface = pixelState.Surface;
                    var canvas = surface.Canvas;
                    canvas.Clear(new SKColor(_theme.Background.R, _theme.Background.G, _theme.Background.B));
                    painter.Paint(canvas, layout);
                    surface.Flush();
                }

                // ─── Upload + present ─────────────────────────────────
                SDL_UpdateTexture(texture, null, pixelState.PixelsPtr, pixelState.RowBytes);
                SDL_RenderClear(renderer);
                SDL_RenderTexture(renderer, texture, null, null);
                SDL_RenderPresent(renderer);
            }
        }
        finally
        {
            if (texture is not null) SDL_DestroyTexture(texture);
            pixelState.Dispose();
        }
    }

    private void TryDispatchClick(int x, int y, LayoutNode? lastLayout)
    {
        if (lastLayout is null) return;
        var hit = HitTest.Find(lastLayout, x, y);
        if (hit is null) return;
        if (hit.Source.Props.TryGetValue("onClick", out var v) && v is Delegate d)
        {
            try
            {
                if (d.DynamicInvoke() is TMsg msg)
                    _runner.Dispatch(msg);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[kohui-skia] click handler threw at {hit.Path}: {ex.Message}");
            }
        }
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

    /// <summary>
    /// Holds the pinned pixel array and matching Skia surface. Managed
    /// by the backend rather than the painter because its size tracks
    /// the native window.
    /// </summary>
    private sealed class PixelState : IDisposable
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int RowBytes { get; private set; }
        public IntPtr PixelsPtr { get; private set; }
        public SKSurface Surface { get; private set; } = null!;
        public LayoutNode? LastLayout { get; set; }

        private GCHandle _handle;
        private byte[]? _pixels;

        public void Resize(int w, int h)
        {
            Dispose();
            Width = w;
            Height = h;
            var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            RowBytes = info.RowBytes;
            _pixels = new byte[info.BytesSize];
            _handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
            PixelsPtr = _handle.AddrOfPinnedObject();
            Surface = SKSurface.Create(info, PixelsPtr, info.RowBytes)
                ?? throw new InvalidOperationException("SKSurface.Create returned null on resize");
        }

        public void Dispose()
        {
            Surface?.Dispose();
            if (_handle.IsAllocated) _handle.Free();
            _pixels = null;
            PixelsPtr = IntPtr.Zero;
            Surface = null!;
        }
    }
}
