using KohUI.Backends.Sdl3;
using KohUI.Theme;
using SDL3;

namespace KohUI.Sdl3.Tests;

[NotInParallel]
public class RendererTests
{
    [Test]
    public void Font_Atlas_Pixels_Are_Exact()
    {
        using var context = new SdlContext(
            BitmapFont.GlyphW * BitmapFont.GlyphCount,
            BitmapFont.GlyphH
        )
        {
            CaptureEnabled = true,
        };
        using var batch = new QuadBatch(context);
        using var font = new BitmapFont(context);
        Require(
            context.BeginFrame(font.AtlasWidth, font.AtlasHeight, 255, 255, 255),
            "font atlas frame"
        );
        batch.BeginFrame(font.AtlasWidth, font.AtlasHeight);
        string text = new(
            Enumerable
                .Range(BitmapFont.FirstChar, BitmapFont.GlyphCount)
                .Select(ch => (char)ch)
                .ToArray()
        );
        font.DrawString(batch, text, 0, 0, 0, 0, 0);
        batch.Flush();
        context.EndFrame();
        byte[] actual = context.CaptureFrameRgba();
        byte[] mask = BitmapFont.BuildAtlasPixels();
        for (int i = 0; i < mask.Length; i++)
        {
            byte expected = (byte)(255 - mask[i]);
            Require(
                actual[i * 4] == expected
                    && actual[i * 4 + 1] == expected
                    && actual[i * 4 + 2] == expected
                    && actual[i * 4 + 3] == 255,
                $"font atlas pixel {i % font.AtlasWidth},{i / font.AtlasWidth}"
            );
        }
    }

    [Test]
    public void Image_Uses_Integer_Scale_In_Stretched_Layout()
    {
        using var context = new SdlContext(20, 20) { CaptureEnabled = true };
        using var batch = new QuadBatch(context);
        using var font = new BitmapFont(context);
        using var painter = new Painter(Win98Theme.Default, batch, font, context);
        var node = new LayoutNode
        {
            Source = RenderNode.Leaf(
                "Image",
                Props.Of(
                    ("pixels", new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }),
                    ("width", 2),
                    ("height", 1),
                    ("scale", 2)
                )
            ),
            Path = "",
            Bounds = new Rect(0, 0, 20, 20),
            Children = [],
        };
        Require(context.BeginFrame(20, 20, 0, 0, 0), "image scale frame");
        batch.BeginFrame(20, 20);
        painter.Paint(node);
        batch.Flush();
        context.EndFrame();
        byte[] pixels = context.CaptureFrameRgba();
        Require(pixels[(1 * 20 + 1) * 4] == 255, "red texel at integer scale");
        Require(pixels[(1 * 20 + 2) * 4 + 1] == 255, "green texel at integer scale");
        Require(pixels[(1 * 20 + 4) * 4 + 1] == 0, "image width is not stretched");
        Require(pixels[(2 * 20 + 1) * 4] == 0, "image height is not stretched");
    }

    [Test]
    public void Software_Pixels_And_Repeated_Lifetime()
    {
        for (int iteration = 0; iteration < 3; iteration++)
            foreach (var size in new[] { (64, 64), (96, 80), (256, 256) })
            {
                using var context = new SdlContext(size.Item1, size.Item2)
                {
                    CaptureEnabled = true,
                };
                CheckRenderer(context);
            }
    }

    [Test]
    public void Window_Rendering_And_Resize()
    {
        if (Environment.GetEnvironmentVariable("KOH_SDL_WINDOW_TEST") != "1")
            Skip.Test("Set KOH_SDL_WINDOW_TEST=1 to exercise native windows");
        Require(SDL.Init(SDL.InitFlags.Video), "SDL video initialization");
        try
        {
            nint window = SDL.CreateWindow(
                "SDL regression",
                64,
                64,
                SDL.WindowFlags.Resizable | SDL.WindowFlags.HighPixelDensity
            );
            Require(window != 0, "SDL window");
            try
            {
                using var context = new SdlContext(window) { CaptureEnabled = true };
                CheckRenderer(context);
                float density = SDL.GetWindowPixelDensity(window);
                Require(density > 0, "SDL window pixel density");
                Require(SDL.SetWindowSize(window, 96, 80), "SDL resize");
                int pixelWidth = 0,
                    pixelHeight = 0;
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    SDL.PumpEvents();
                    Require(context.BeginFrame(64, 64, 0, 0, 0), "SDL resized frame");
                    context.EndFrame();
                    Require(
                        SDL.GetWindowSizeInPixels(window, out pixelWidth, out pixelHeight),
                        "SDL native framebuffer size"
                    );
                    if (
                        pixelWidth == (int)(96 * density)
                        && pixelHeight == (int)(80 * density)
                        && context.FramebufferWidth == pixelWidth
                        && context.FramebufferHeight == pixelHeight
                    )
                        break;
                    Thread.Sleep(10);
                }
                Require(
                    context.FramebufferWidth == pixelWidth
                        && context.FramebufferHeight == pixelHeight
                        && pixelWidth == (int)(96 * density)
                        && pixelHeight == (int)(80 * density),
                    "resized framebuffer"
                );
                Console.WriteLine(
                    $"SDL window density {density}; resized native pixels {pixelWidth}x{pixelHeight}"
                );
                CheckRenderer(context);
                CheckWindowFont(context);
            }
            finally
            {
                SDL.DestroyWindow(window);
            }
        }
        finally
        {
            SDL.Quit();
        }
    }

    private static void CheckWindowFont(SdlContext context)
    {
        using var batch = new QuadBatch(context);
        using var font = new BitmapFont(context);
        Require(context.BeginFrame(96, 80, 255, 255, 255), "window font frame");
        batch.BeginFrame(96, 80);
        string text = new(
            Enumerable
                .Range(BitmapFont.FirstChar, BitmapFont.GlyphCount)
                .Select(ch => (char)ch)
                .ToArray()
        );
        for (int at = 0; at < text.Length; at += 16)
            font.DrawString(
                batch,
                text.Substring(at, Math.Min(16, text.Length - at)),
                0,
                at / 16 * 8,
                0,
                0,
                0
            );
        batch.Flush();
        context.EndFrame();
        byte[] actual = context.CaptureFrameRgba();
        byte[] mask = BitmapFont.BuildAtlasPixels();
        for (int py = 0; py < context.FramebufferHeight; py++)
        for (int px = 0; px < context.FramebufferWidth; px++)
        {
            int x = (int)((px + 0.5) * 96 / context.FramebufferWidth);
            int y = (int)((py + 0.5) * 80 / context.FramebufferHeight);
            int glyph = y / 8 * 16 + x / 6;
            byte expected =
                glyph < BitmapFont.GlyphCount
                    ? (byte)(255 - mask[y % 8 * font.AtlasWidth + glyph * 6 + x % 6])
                    : (byte)255;
            int offset = (py * context.FramebufferWidth + px) * 4;
            Require(
                actual[offset] == expected
                    && actual[offset + 1] == expected
                    && actual[offset + 2] == expected
                    && actual[offset + 3] == 255,
                $"window font pixel {px},{py}"
            );
        }
    }

    private static void CheckRenderer(SdlContext context)
    {
        byte[] original = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 0];
        byte[] input = (byte[])original.Clone();
        uint image = context.CreateTexture(2, 2, input);
        using (var batch = new QuadBatch(context))
        using (var font = new BitmapFont(context))
        {
            Require(context.BeginFrame(64, 64, 0, 0, 0), "SDL frame");
            batch.BeginFrame(64, 64);
            batch.SetTexture(image);
            batch.TexturedQuad(0, 0, 32, 32, 0, 0, 1, 1, 255, 255, 255);
            batch.FillRect(32, 0, 16, 16, 255, 0, 0, 128);
            batch.Flush();
            context.Draw(Quad(32, 16, 32, 16), image, new Rect(40, 20, 8, 8));
            font.DrawString(batch, "A", 0, 40, 255, 255, 255);
            batch.Flush();
            batch.SetClip(new Rect(16, 40, 2, 8));
            font.DrawString(batch, "A", 16, 40, 255, 255, 255);
            batch.Flush();
            batch.SetClip(null);
            context.EndFrame();
            byte[] pixels = context.CaptureFrameRgba();
            Require(
                pixels.Length == context.FramebufferWidth * context.FramebufferHeight * 4,
                "RGBA readback length"
            );
            Pixel(context, pixels, 4, 4, 255, 0, 0);
            Pixel(context, pixels, 15, 4, 255, 0, 0);
            Pixel(context, pixels, 16, 4, 0, 255, 0);
            Pixel(context, pixels, 4, 24, 0, 0, 255);
            Pixel(context, pixels, 24, 24, 0, 0, 0);
            Pixel(context, pixels, 36, 4, 128, 0, 0);
            Pixel(context, pixels, 39, 24, 0, 0, 0);
            Pixel(context, pixels, 40, 24, 0, 0, 255);
            Pixel(context, pixels, 48, 24, 0, 0, 0);
            Pixel(context, pixels, 0, 40, 0, 0, 0);
            Pixel(context, pixels, 1, 40, 255, 255, 255);
            Pixel(context, pixels, 0, 43, 255, 255, 255);
            Pixel(context, pixels, 17, 40, 255, 255, 255);
            Pixel(context, pixels, 18, 40, 0, 0, 0);
            Require(input.AsSpan().SequenceEqual(original), "source RGBA buffer unchanged");
            context.UpdateTexture(
                image,
                [0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255, 0, 255, 255, 255]
            );
            Require(context.BeginFrame(64, 64, 0, 0, 0), "updated image frame");
            context.Draw(Quad(0, 0, 32, 32), image, null);
            context.EndFrame();
            Pixel(context, context.CaptureFrameRgba(), 4, 4, 0, 255, 255);
            if (context.FramebufferWidth == 256 && context.FramebufferHeight == 256)
                Gallery(context, batch, font);
        }
        context.DeleteTexture(image);
    }

    private static void Gallery(SdlContext context, QuadBatch batch, BitmapFont font)
    {
        var theme = Win98Theme.Default with { Background = new KohColor(31, 47, 63) };
        var layouter = new Layouter(theme);
        using var painter = new Painter(theme, batch, font, context);
        RenderNode Image(byte r, byte g, byte b) =>
            RenderNode.Leaf(
                "Image",
                Props.Of(
                    ("pixels", new byte[] { r, g, b, 255 }),
                    ("width", 1),
                    ("height", 1),
                    ("scale", 32)
                )
            );
        for (int frame = 0; frame < 16; frame++)
        {
            bool moved = frame % 2 != 0;
            var source = RenderNode.WithChildren(
                "Window",
                [
                    RenderNode.Leaf(
                        "Button",
                        Props.Of(("text", "&OK"), ("onClick", (Action)(() => { })))
                    ),
                    RenderNode.Leaf("CheckBox", Props.Of(("text", "Checked"), ("checked", true))),
                    RenderNode.WithChildren(
                        "Panel",
                        [RenderNode.Leaf("Label", Props.Of(("text", "Panel")))],
                        Props.Of(("bevel", "Sunken"))
                    ),
                    RenderNode.WithChildren(
                        "Stack",
                        [
                            RenderNode.WithChildren(
                                "ScrollPanel",
                                [Image(0, 255, 255)],
                                Props.Of(("viewportW", 16), ("viewportH", 16))
                            ),
                        ],
                        Props.Of(("direction", "Horizontal"))
                    ),
                    moved
                        ? RenderNode.Leaf("Label", Props.Of(("text", "Moved")))
                        : Image(255, 0, 255),
                    moved
                        ? Image(255, 255, 0)
                        : RenderNode.Leaf("Label", Props.Of(("text", "Before"))),
                ]
            );
            var tree = layouter.Layout(source, 256, 256);
            var order = Focus.Enumerate(tree);
            Require(
                order.Length == 1 && Focus.Next(order, null) == tree.Children[0].Path,
                "gallery focus order"
            );
            Require(context.BeginFrame(256, 256, 0, 0, 0), "gallery frame");
            batch.BeginFrame(256, 256);
            painter.Paint(tree, focusPath: order[0]);
            batch.Flush();
            uint transient = context.CreateTexture(1, 1, [255, 255, 255, 255]);
            context.Draw(Quad(200, 200, 8, 8), transient, null);
            context.EndFrame();
            context.DeleteTexture(transient);
            if (frame < 12)
                continue;
            byte[] pixels = context.CaptureFrameRgba();
            var scroll = tree.Children[3].Children[0].Bounds;
            GalleryPixel(context, pixels, scroll.X + 8, scroll.Y + 8, 0, 255, 255);
            GalleryPixel(
                context,
                pixels,
                scroll.Right + 1,
                scroll.Y + 8,
                theme.Background.R,
                theme.Background.G,
                theme.Background.B
            );
            var image = tree.Children[moved ? 5 : 4].Bounds;
            GalleryPixel(
                context,
                pixels,
                image.X + 8,
                image.Y + 8,
                255,
                moved ? (byte)255 : (byte)0,
                moved ? (byte)0 : (byte)255
            );
            GalleryPixel(context, pixels, 204, 204, 255, 255, 255);
        }
    }

    private static SDL.Vertex[] Quad(int x, int y, int w, int h)
    {
        SDL.Vertex Vertex(float px, float py) =>
            new()
            {
                Position = new SDL.FPoint { X = px, Y = py },
                Color = new SDL.FColor(1, 1, 1, 1),
                TexCoord = new SDL.FPoint { X = px == x ? 0 : 1, Y = py == y ? 0 : 1 },
            };
        return [Vertex(x, y), Vertex(x + w, y), Vertex(x + w, y + h), Vertex(x, y + h)];
    }

    private static void Pixel(
        SdlContext context,
        byte[] pixels,
        int x,
        int y,
        byte r,
        byte g,
        byte b
    )
    {
        int px = Math.Min(
            context.FramebufferWidth - 1,
            (int)((x + 0.5) * context.FramebufferWidth / 64)
        );
        int py = Math.Min(
            context.FramebufferHeight - 1,
            (int)((y + 0.5) * context.FramebufferHeight / 64)
        );
        GalleryPixel(context, pixels, px, py, r, g, b);
    }

    private static void GalleryPixel(
        SdlContext context,
        byte[] pixels,
        int x,
        int y,
        byte r,
        byte g,
        byte b
    )
    {
        int offset = (y * context.FramebufferWidth + x) * 4;
        Require(
            Math.Abs(pixels[offset] - r) <= 1
                && Math.Abs(pixels[offset + 1] - g) <= 1
                && Math.Abs(pixels[offset + 2] - b) <= 1,
            $"pixel ({x},{y}): expected {r},{g},{b}; got {pixels[offset]},{pixels[offset + 1]},{pixels[offset + 2]}"
        );
        Require(pixels[offset + 3] == 255, $"pixel ({x},{y}): opaque framebuffer alpha");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"{message}: {SDL.GetError()}");
    }
}
