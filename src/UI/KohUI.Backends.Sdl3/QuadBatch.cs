using SDL3;

namespace KohUI.Backends.Sdl3;

internal sealed class QuadBatch : IDisposable
{
    private const int VertsPerQuad = 4;
    private readonly SdlContext _context;
    private readonly SDL.Vertex[] _verts = new SDL.Vertex[4096 * VertsPerQuad];
    private readonly uint _whitePixel;
    private int _count;
    private uint _currentTexture;
    private Rect? _clip;

    public QuadBatch(SdlContext context)
    {
        _context = context;
        _whitePixel = context.CreateTexture(1, 1, [255, 255, 255, 255]);
        _currentTexture = _whitePixel;
    }

    public void BeginFrame(int width, int height)
    {
        _count = 0;
        _currentTexture = _whitePixel;
        _clip = null;
    }

    public void SetClip(Rect? clip)
    {
        if (_clip == clip)
            return;
        Flush();
        _clip = clip;
    }

    public void FillRect(int x, int y, int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        if (w <= 0 || h <= 0)
            return;
        EnsureTexture(_whitePixel);
        AddQuad(x, y, w, h, 0, 0, 1, 1, r, g, b, a);
    }

    public void TexturedQuad(
        int x,
        int y,
        int w,
        int h,
        float u0,
        float v0,
        float u1,
        float v1,
        byte r,
        byte g,
        byte b,
        byte a = 255
    )
    {
        if (w <= 0 || h <= 0)
            return;
        AddQuad(x, y, w, h, u0, v0, u1, v1, r, g, b, a);
    }

    public void SetTexture(uint texture)
    {
        EnsureTexture(texture);
    }

    private void EnsureTexture(uint texture)
    {
        if (texture != _currentTexture)
        {
            Flush();
            _currentTexture = texture;
        }
    }

    private void AddQuad(
        int x,
        int y,
        int w,
        int h,
        float u0,
        float v0,
        float u1,
        float v1,
        byte r,
        byte g,
        byte b,
        byte a
    )
    {
        if (_count + VertsPerQuad > _verts.Length)
            Flush();

        float x1 = x + w,
            y1 = y + h;
        var color = new SDL.FColor
        {
            R = r / 255f,
            G = g / 255f,
            B = b / 255f,
            A = a / 255f,
        };
        var v00 = new SDL.Vertex
        {
            Position = new SDL.FPoint { X = x, Y = y },
            TexCoord = new SDL.FPoint { X = u0, Y = v0 },
            Color = color,
        };
        var v10 = new SDL.Vertex
        {
            Position = new SDL.FPoint { X = x1, Y = y },
            TexCoord = new SDL.FPoint { X = u1, Y = v0 },
            Color = color,
        };
        var v11 = new SDL.Vertex
        {
            Position = new SDL.FPoint { X = x1, Y = y1 },
            TexCoord = new SDL.FPoint { X = u1, Y = v1 },
            Color = color,
        };
        var v01 = new SDL.Vertex
        {
            Position = new SDL.FPoint { X = x, Y = y1 },
            TexCoord = new SDL.FPoint { X = u0, Y = v1 },
            Color = color,
        };

        _verts[_count++] = v00;
        _verts[_count++] = v10;
        _verts[_count++] = v11;
        _verts[_count++] = v01;
    }

    public void Flush()
    {
        if (_count == 0)
            return;
        _context.Draw(_verts.AsSpan(0, _count), _currentTexture, _clip);
        _count = 0;
    }

    public void Dispose() => _context.DeleteTexture(_whitePixel);
}
