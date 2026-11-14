using SDL3;

namespace KohUI.Backends.Sdl3;

internal sealed unsafe class SdlContext : IDisposable
{
    private readonly nint _window;
    private nint _renderer;
    private nint _surface;
    private readonly Dictionary<uint, Texture> _textures = [];
    private uint _nextTexture = 1;
    private byte[]? _capture;
    private bool _active,
        _disposed;
    private static SDL.PixelFormat Rgba32 =>
        BitConverter.IsLittleEndian ? SDL.PixelFormat.ABGR8888 : SDL.PixelFormat.RGBA8888;

    internal int FramebufferWidth { get; private set; }
    internal int FramebufferHeight { get; private set; }
    internal bool CaptureEnabled { get; set; }
    internal string RendererName { get; }

    private readonly record struct Texture(nint Handle, int Width, int Height);

    public SdlContext(nint window)
    {
        if (window == 0)
            throw new ArgumentException("An SDL window is required.", nameof(window));
        _window = window;
        _renderer = CreateRenderer(window);
        RendererName = SDL.GetRendererName(_renderer) ?? "unknown";
        SDL.SetRenderVSync(_renderer, 1);
    }

    public SdlContext(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        try
        {
            _surface = SDL.CreateSurface(width, height, Rgba32);
            if (_surface == 0)
                throw Error("create software surface");
            _renderer = SDL.CreateSoftwareRenderer(_surface);
            if (_renderer == 0)
                throw Error("create software renderer");
            RendererName = SDL.GetRendererName(_renderer) ?? "software";
            FramebufferWidth = width;
            FramebufferHeight = height;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static nint CreateRenderer(nint window)
    {
        string? requested = SDL.GetHint(SDL.Hints.RenderDriver);
        var available = new List<string>();
        for (int i = 0; i < SDL.GetNumRenderDrivers(); i++)
        {
            var name = SDL.GetRenderDriver(i);
            if (name != null && !name.StartsWith("opengl", StringComparison.OrdinalIgnoreCase))
                available.Add(name);
        }
        IEnumerable<string> candidates;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var names = requested.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            if (names.Any(name => name.StartsWith("opengl", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "SDL_RENDER_DRIVER must select an SDL renderer without OpenGL."
                );
            candidates = names;
        }
        else
        {
            candidates = new[] { "vulkan", "metal", "direct3d12", "direct3d11", "direct3d" }
                .Where(available.Contains)
                .Concat(
                    available.Where(name =>
                        name != "software"
                        && name
                            is not (
                                "vulkan"
                                or "metal"
                                or "direct3d12"
                                or "direct3d11"
                                or "direct3d"
                            )
                    )
                )
                .Append("software");
        }
        var failures = new List<string>();
        foreach (var name in candidates)
        {
            if (name.Equals("software", StringComparison.OrdinalIgnoreCase))
            {
                string presentation = SDL.GetCurrentVideoDriver() == "wayland" ? "vulkan" : "0";
                Check(
                    SDL.SetHintWithPriority(
                        SDL.Hints.FramebufferAcceleration,
                        presentation,
                        SDL.HintPriority.Override
                    ),
                    "set software framebuffer presentation"
                );
            }
            nint renderer = SDL.CreateRenderer(window, name);
            if (renderer != 0)
                return renderer;
            failures.Add($"{name}: {SDL.GetError()}");
        }
        throw new InvalidOperationException(
            $"No SDL renderer could be initialized. {string.Join("; ", failures)}"
        );
    }

    public uint CreateTexture(int width, int height, ReadOnlySpan<byte> rgba)
    {
        EnsureUsable();
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("Texture dimensions and RGBA byte count must match.");
        nint handle = SDL.CreateTexture(
            _renderer,
            Rgba32,
            SDL.TextureAccess.Streaming,
            width,
            height
        );
        if (handle == 0)
            throw Error("create texture");
        uint id = 0;
        try
        {
            Check(
                SDL.SetTextureScaleMode(handle, SDL.ScaleMode.Nearest),
                "set nearest texture filtering"
            );
            Check(SDL.SetTextureBlendMode(handle, SDL.BlendMode.Blend), "set texture blending");
            var texture = new Texture(handle, width, height);
            WriteTexture(texture, rgba);
            id = checked(_nextTexture++);
            _textures.Add(id, texture);
            return id;
        }
        catch
        {
            if (id != 0)
                _textures.Remove(id);
            SDL.DestroyTexture(handle);
            throw;
        }
    }

    public void UpdateTexture(uint texture, ReadOnlySpan<byte> rgba)
    {
        EnsureUsable();
        var resource = _textures[texture];
        if (rgba.Length != checked(resource.Width * resource.Height * 4))
            throw new ArgumentException("RGBA byte count must match the texture.", nameof(rgba));
        WriteTexture(resource, rgba);
    }

    private static void WriteTexture(Texture texture, ReadOnlySpan<byte> rgba)
    {
        Check(
            SDL.LockTexture(texture.Handle, 0, out nint pixels, out int pitch),
            "lock streaming texture"
        );
        try
        {
            int rowBytes = checked(texture.Width * 4);
            for (int row = 0; row < texture.Height; row++)
                rgba.Slice(row * rowBytes, rowBytes)
                    .CopyTo(new Span<byte>((byte*)pixels + (nint)row * pitch, rowBytes));
        }
        finally
        {
            SDL.UnlockTexture(texture.Handle);
        }
    }

    public void DeleteTexture(uint texture)
    {
        if (_textures.Remove(texture, out var resource))
            SDL.DestroyTexture(resource.Handle);
    }

    public bool BeginFrame(int logicalWidth, int logicalHeight, byte r, byte g, byte b)
    {
        EnsureUsable();
        if (_active)
            throw new InvalidOperationException("An SDL frame is already active.");
        if (logicalWidth <= 0 || logicalHeight <= 0)
            return false;
        if (_window != 0 && (SDL.GetWindowFlags(_window) & SDL.WindowFlags.Minimized) != 0)
            return false;
        Check(
            SDL.GetRenderOutputSize(_renderer, out int width, out int height),
            "get render output size"
        );
        if (width <= 0 || height <= 0)
            return false;
        FramebufferWidth = width;
        FramebufferHeight = height;
        Check(
            SDL.SetRenderScale(
                _renderer,
                (float)width / logicalWidth,
                (float)height / logicalHeight
            ),
            "set logical render scale"
        );
        Check(SDL.SetRenderClipRect(_renderer, 0), "reset clip rectangle");
        Check(SDL.SetRenderDrawColor(_renderer, r, g, b, 255), "set clear color");
        Check(SDL.RenderClear(_renderer), "clear frame");
        _capture = null;
        _active = true;
        return true;
    }

    public void Draw(ReadOnlySpan<SDL.Vertex> vertices, uint texture, Rect? clip)
    {
        EnsureUsable();
        if (!_active)
            throw new InvalidOperationException("BeginFrame must precede Draw.");
        if (vertices.Length == 0)
            return;
        if (vertices.Length % 4 != 0)
            throw new ArgumentException("A quad requires four vertices.", nameof(vertices));
        if (clip is { } rectangle)
        {
            if (rectangle.W <= 0 || rectangle.H <= 0)
                return;
            SDL.Rect nativeClip = new()
            {
                X = rectangle.X,
                Y = rectangle.Y,
                W = rectangle.W,
                H = rectangle.H,
            };
            Check(SDL.SetRenderClipRect(_renderer, in nativeClip), "set clip rectangle");
        }
        else
            Check(SDL.SetRenderClipRect(_renderer, 0), "reset clip rectangle");
        var resource = _textures[texture];
        for (int i = 0; i < vertices.Length; i += 4)
        {
            var first = vertices[i];
            var last = vertices[i + 2];
            float x = MathF.Round(first.TexCoord.X * resource.Width);
            float y = MathF.Round(first.TexCoord.Y * resource.Height);
            SDL.FRect source = new()
            {
                X = x,
                Y = y,
                W = MathF.Round(last.TexCoord.X * resource.Width) - x,
                H = MathF.Round(last.TexCoord.Y * resource.Height) - y,
            };
            SDL.FRect destination = new()
            {
                X = first.Position.X,
                Y = first.Position.Y,
                W = last.Position.X - first.Position.X,
                H = last.Position.Y - first.Position.Y,
            };
            Check(
                SDL.SetTextureColorModFloat(
                    resource.Handle,
                    first.Color.R,
                    first.Color.G,
                    first.Color.B
                ),
                "set texture color"
            );
            Check(SDL.SetTextureAlphaModFloat(resource.Handle, first.Color.A), "set texture alpha");
            Check(
                SDL.RenderTexture(_renderer, resource.Handle, in source, in destination),
                "draw rectangle"
            );
        }
    }

    public void EndFrame()
    {
        EnsureUsable();
        if (!_active)
            throw new InvalidOperationException("No SDL frame is active.");
        try
        {
            if (CaptureEnabled)
                _capture = ReadPixels();
            Check(SDL.RenderPresent(_renderer), "present frame");
        }
        finally
        {
            _active = false;
        }
    }

    internal byte[] CaptureFrameRgba()
    {
        EnsureUsable();
        return _capture
            ?? throw new InvalidOperationException(
                "Enable CaptureEnabled before rendering a frame."
            );
    }

    private byte[] ReadPixels()
    {
        Check(SDL.SetRenderClipRect(_renderer, 0), "reset capture clip");
        nint surface = SDL.RenderReadPixels(_renderer, null);
        if (surface == 0)
            throw Error("read frame pixels");
        nint converted = 0;
        try
        {
            converted = SDL.ConvertSurface(surface, Rgba32);
            if (converted == 0)
                throw Error("convert frame pixels");
            Check(SDL.LockSurface(converted), "lock capture surface");
            try
            {
                var pixels = (SDL.Surface*)converted;
                int rowBytes = checked(pixels->Width * 4);
                var result = new byte[checked(rowBytes * pixels->Height)];
                for (int row = 0; row < pixels->Height; row++)
                    new ReadOnlySpan<byte>(
                        (byte*)pixels->Pixels + (nint)row * pixels->Pitch,
                        rowBytes
                    ).CopyTo(result.AsSpan(row * rowBytes, rowBytes));
                return result;
            }
            finally
            {
                SDL.UnlockSurface(converted);
            }
        }
        finally
        {
            if (converted != 0)
                SDL.DestroySurface(converted);
            SDL.DestroySurface(surface);
        }
    }

    private static InvalidOperationException Error(string operation) =>
        new($"SDL {operation} failed: {SDL.GetError()}");

    private static void Check(bool success, string operation)
    {
        if (!success)
            throw Error(operation);
    }

    private void EnsureUsable() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var texture in _textures.Values)
            SDL.DestroyTexture(texture.Handle);
        _textures.Clear();
        if (_renderer != 0)
            SDL.DestroyRenderer(_renderer);
        if (_surface != 0)
            SDL.DestroySurface(_surface);
        _renderer = 0;
        _surface = 0;
    }
}
