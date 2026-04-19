using System.Runtime.InteropServices;
using KohUI;
using KohUI.Theme;
using SDL;
using SkiaSharp;
using static SDL.SDL3;

namespace KohUI.Backends.Skia;

/// <summary>
/// Cross-platform native backend. Opens one SDL3 window, draws the
/// MVU render tree into an <see cref="SKSurface"/>, streams the pixels
/// into an SDL texture, and blits via <c>SDL_Renderer</c>. Pure CPU
/// rendering for now — a retro UI drawing a few hundred rects and a
/// label per frame doesn't need GPU. GL/Vulkan acceleration via
/// SkiaSharp's <c>GRContext</c> comes in a later phase; it's additive
/// rather than blocking.
///
/// <para>
/// <b>First drop scope:</b> opens the window, clears the background
/// to <see cref="Win98Theme.Background"/>, finds the first Label in
/// the current render tree, and draws its text at a fixed position.
/// Events are polled but not yet dispatched into the runner — that
/// becomes the second drop once rectangles and hit-testing land.
/// </para>
/// </summary>
public sealed class SkiaBackend<TModel, TMsg>
{
    private readonly Runner<TModel, TMsg> _runner;
    private readonly Win98Theme _theme;
    private readonly string _title;
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
        _title = title;
        _width = width;
        _height = height;
    }

    public unsafe void Run()
    {
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO))
            throw new InvalidOperationException("SDL_Init failed: " + GetError());

        try
        {
            SDL_Window* window;
            fixed (byte* titleUtf8 = System.Text.Encoding.UTF8.GetBytes(_title + "\0"))
                window = SDL_CreateWindow(titleUtf8, _width, _height, 0);
            if (window is null)
                throw new InvalidOperationException("SDL_CreateWindow failed: " + GetError());

            SDL_Renderer* renderer = SDL_CreateRenderer(window, (byte*)null);
            if (renderer is null)
                throw new InvalidOperationException("SDL_CreateRenderer failed: " + GetError());

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
        var info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixels = new byte[info.BytesSize];

        // Pin the managed array for the life of the loop; SDL uploads
        // from this pointer each frame, Skia writes into it.
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var surface = SKSurface.Create(info, handle.AddrOfPinnedObject(), info.RowBytes)
                ?? throw new InvalidOperationException("SKSurface.Create returned null");

            SDL_Texture* texture = SDL_CreateTexture(renderer,
                SDL_PixelFormat.SDL_PIXELFORMAT_BGRA8888,
                SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
                _width, _height);
            if (texture is null)
                throw new InvalidOperationException("SDL_CreateTexture failed: " + GetError());

            try
            {
                var canvas = surface.Canvas;
                var bgColor = Convert(_theme.Background);
                using var textPaint = new SKPaint { Color = Convert(_theme.Text), IsAntialias = true };
                using var font = new SKFont
                {
                    Typeface = SKTypeface.FromFamilyName(_theme.UiFontFamily) ?? SKTypeface.Default,
                    Size = _theme.UiFontSize + 5f,   // bump for legibility until kerning + ClearType land
                };

                var ev = default(SDL_Event);
                bool running = true;
                while (running)
                {
                    while (SDL_PollEvent(&ev))
                    {
                        if (ev.type == (uint)SDL_EventType.SDL_EVENT_QUIT)
                            running = false;
                    }

                    // ─── Draw ───────────────────────────────────────
                    canvas.Clear(bgColor);

                    var tree = _runner.CurrentRender;
                    if (tree is not null)
                    {
                        var label = FindFirstLabel(tree);
                        if (label is { } text)
                            canvas.DrawText(text, 20, 40, font, textPaint);
                    }

                    surface.Flush();

                    // ─── Upload + blit ──────────────────────────────
                    SDL_UpdateTexture(texture, null, handle.AddrOfPinnedObject(), info.RowBytes);
                    SDL_RenderClear(renderer);
                    SDL_RenderTexture(renderer, texture, null, null);
                    SDL_RenderPresent(renderer);
                }
            }
            finally
            {
                SDL_DestroyTexture(texture);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private static SKColor Convert(KohColor c) => new(c.R, c.G, c.B);

    /// <summary>
    /// Walks the render tree top-down and returns the first Label's
    /// text. Enough for the first-drop "stack is alive" smoke test;
    /// proper rendering of the whole tree lands once bounds + hit-
    /// testing are in.
    /// </summary>
    private static string? FindFirstLabel(RenderNode node)
    {
        if (node.Type == "Label" && node.Props.TryGetValue("text", out var t) && t is string s)
            return s;
        foreach (var child in node.Children)
        {
            var found = FindFirstLabel(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static string GetError() => SDL_GetError() ?? "";
}
