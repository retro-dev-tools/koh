using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace KohUI.Backends.Skia;

/// <summary>
/// Minimal immediate-mode quad batcher on top of GL 3.3 core. Accumulates
/// textured + coloured quads into a vertex buffer, draws in a single call
/// per <see cref="Flush"/>. Plenty for a retro UI that draws a few
/// hundred bevels + glyphs per frame.
///
/// <para>
/// Vertex: <c>pos.xy, uv.xy, rgba</c> (24 bytes). A 1×1 white texture
/// makes untextured fills a single-multiply no-op (<c>colour × 1</c>),
/// so the whole pipeline is one shader with one texture slot.
/// </para>
/// </summary>
internal sealed unsafe class QuadBatch : IDisposable
{
    private const int MaxQuads = 4096;
    private const int VertsPerQuad = 6;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Vertex
    {
        public float X, Y;
        public float U, V;
        public byte R, G, B, A;
    }

    private readonly GL _gl;
    private readonly Vertex[] _verts = new Vertex[MaxQuads * VertsPerQuad];
    private int _count;    // current vertex count
    private uint _currentTexture;

    private readonly uint _program;
    private readonly int _uViewportLoc;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly uint _whitePixel;

    private int _viewportW, _viewportH;

    public QuadBatch(GL gl)
    {
        _gl = gl;

        _program = BuildProgram(gl, VertexShader, FragmentShader);
        _uViewportLoc = gl.GetUniformLocation(_program, "uViewport");
        int uTexLoc    = gl.GetUniformLocation(_program, "uTex");
        gl.UseProgram(_program);
        gl.Uniform1(uTexLoc, 0);

        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData(BufferTargetARB.ArrayBuffer,
            (nuint)(_verts.Length * sizeof(Vertex)), null, BufferUsageARB.DynamicDraw);

        // Attribute 0: position (2 floats), offset 0
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex), (void*)0);
        // Attribute 1: uv (2 floats), offset 8
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, (uint)sizeof(Vertex), (void*)8);
        // Attribute 2: colour (4 bytes normalised), offset 16
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, (uint)sizeof(Vertex), (void*)16);

        // 1×1 white texture used as the default sampler target so
        // untextured fills become `colour * 1`.
        _whitePixel = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _whitePixel);
        byte[] px = [255, 255, 255, 255];
        fixed (byte* p = px)
            gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _currentTexture = _whitePixel;
    }

    public void BeginFrame(int width, int height)
    {
        _viewportW = width;
        _viewportH = height;
        _gl.Viewport(0, 0, (uint)width, (uint)height);
        _count = 0;
        _currentTexture = _whitePixel;
    }

    /// <summary>
    /// Fill an axis-aligned rectangle in client-area pixel coordinates
    /// (0,0 = top-left).
    /// </summary>
    public void FillRect(int x, int y, int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        if (w <= 0 || h <= 0) return;
        EnsureTexture(_whitePixel);
        AddQuad(x, y, w, h, 0, 0, 1, 1, r, g, b, a);
    }

    /// <summary>
    /// Textured quad with UV coordinates in atlas-pixel space. The atlas
    /// is uploaded by <see cref="BitmapFont"/> (or any future texture-
    /// owning widget) and bound via <see cref="SetTexture"/>.
    /// </summary>
    public void TexturedQuad(int x, int y, int w, int h, float u0, float v0, float u1, float v1, byte r, byte g, byte b, byte a = 255)
    {
        if (w <= 0 || h <= 0) return;
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

    private void AddQuad(int x, int y, int w, int h, float u0, float v0, float u1, float v1, byte r, byte g, byte b, byte a)
    {
        if (_count + VertsPerQuad > _verts.Length) Flush();

        float x1 = x + w, y1 = y + h;
        var v00 = new Vertex { X = x, Y = y, U = u0, V = v0, R = r, G = g, B = b, A = a };
        var v10 = new Vertex { X = x1, Y = y, U = u1, V = v0, R = r, G = g, B = b, A = a };
        var v11 = new Vertex { X = x1, Y = y1, U = u1, V = v1, R = r, G = g, B = b, A = a };
        var v01 = new Vertex { X = x, Y = y1, U = u0, V = v1, R = r, G = g, B = b, A = a };

        _verts[_count++] = v00;
        _verts[_count++] = v10;
        _verts[_count++] = v11;
        _verts[_count++] = v00;
        _verts[_count++] = v11;
        _verts[_count++] = v01;
    }

    public void Flush()
    {
        if (_count == 0) return;

        _gl.UseProgram(_program);
        _gl.Uniform2(_uViewportLoc, (float)_viewportW, (float)_viewportH);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _currentTexture);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        fixed (Vertex* p = _verts)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(_count * sizeof(Vertex)), p);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);
        _count = 0;
    }

    // ─── Shaders ─────────────────────────────────────────────────────

    private const string VertexShader = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in vec4 aColor;
        uniform vec2 uViewport;
        out vec2 vUV;
        out vec4 vColor;
        void main() {
            vec2 ndc = vec2(aPos.x / uViewport.x * 2.0 - 1.0,
                            1.0 - aPos.y / uViewport.y * 2.0);
            gl_Position = vec4(ndc, 0.0, 1.0);
            vUV = aUV;
            vColor = aColor;
        }
        """;

    private const string FragmentShader = """
        #version 330 core
        in vec2 vUV;
        in vec4 vColor;
        uniform sampler2D uTex;
        out vec4 outColor;
        void main() {
            outColor = texture(uTex, vUV) * vColor;
        }
        """;

    private static uint BuildProgram(GL gl, string vs, string fs)
    {
        uint vsh = Compile(gl, ShaderType.VertexShader, vs);
        uint fsh = Compile(gl, ShaderType.FragmentShader, fs);
        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vsh);
        gl.AttachShader(prog, fsh);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
            throw new InvalidOperationException("GL link failed: " + gl.GetProgramInfoLog(prog));
        gl.DetachShader(prog, vsh); gl.DeleteShader(vsh);
        gl.DetachShader(prog, fsh); gl.DeleteShader(fsh);
        return prog;
    }

    private static uint Compile(GL gl, ShaderType type, string src)
    {
        uint sh = gl.CreateShader(type);
        gl.ShaderSource(sh, src);
        gl.CompileShader(sh);
        gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
            throw new InvalidOperationException($"GL compile failed ({type}): " + gl.GetShaderInfoLog(sh));
        return sh;
    }

    public void Dispose()
    {
        _gl.DeleteProgram(_program);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteTexture(_whitePixel);
    }
}
