using Silk.NET.GLFW;
using Silk.NET.OpenGL;

namespace KohUI.Backends.Skia;

/// <summary>
/// Owns the GLFW-created OpenGL context and a <see cref="Silk.NET.OpenGL.GL"/>
/// binding to it. The backend calls GL through this — no renderer, no
/// pixel buffer, no Skia.
///
/// <para>
/// OpenGL 3.3 core profile is the target — widely supported on every
/// desktop GPU made in the last decade, AOT-clean through Silk.NET 3.x
/// (<c>LibraryImport</c> + source-gen, no runtime reflection on the hot
/// path), and keeps shader code portable.
/// </para>
/// </summary>
internal sealed unsafe class GlContext : IDisposable
{
    public GL Gl { get; }
    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;

    public GlContext(Glfw glfw, WindowHandle* window)
    {
        _glfw = glfw;
        _window = window;

        _glfw.MakeContextCurrent(window);
        // Vsync on — we render on-demand anyway; vsync keeps the tear
        // line invisible when the MVU loop does redraw.
        _glfw.SwapInterval(1);

        Gl = GL.GetApi(name => (IntPtr)_glfw.GetProcAddress(name));
    }

    public void SwapBuffers() => _glfw.SwapBuffers(_window);

    public void Dispose() => Gl.Dispose();
}
