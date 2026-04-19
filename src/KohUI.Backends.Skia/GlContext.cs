using System.Runtime.InteropServices;
using SDL;
using Silk.NET.OpenGL;
using static SDL.SDL3;

namespace KohUI.Backends.Skia;

/// <summary>
/// Owns the SDL3-created OpenGL context and a <see cref="Silk.NET.OpenGL.GL"/>
/// binding to it. The backend calls the GL functions through this; no
/// SDL_Renderer, no Skia, no CPU pixel buffer.
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
    public IntPtr SdlContext { get; }
    private readonly SDL_Window* _window;

    public GlContext(SDL_Window* window)
    {
        _window = window;

        // Request OpenGL 3.3 core. Mesa on Linux fallbacks, Windows WGL,
        // and macOS CGL all honour these; macOS 10.9+ ships a 4.1 core
        // profile which is a superset.
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MAJOR_VERSION, 3);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_MINOR_VERSION, 3);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_CONTEXT_PROFILE_MASK,
            (int)SDL_GLProfile.SDL_GL_CONTEXT_PROFILE_CORE);
        SDL_GL_SetAttribute(SDL_GLAttr.SDL_GL_DOUBLEBUFFER, 1);

        SdlContext = (IntPtr)SDL_GL_CreateContext(window);
        if (SdlContext == IntPtr.Zero)
            throw new InvalidOperationException("SDL_GL_CreateContext failed: " + (SDL_GetError() ?? ""));

        SDL_GL_MakeCurrent(window, (SDL_GLContextState*)SdlContext);
        // Vsync on — we render on-demand anyway; vsync keeps the tear
        // line invisible when the MVU loop does redraw.
        SDL_GL_SetSwapInterval(1);

        Gl = GL.GetApi(name => (IntPtr)SDL_GL_GetProcAddress(name));
    }

    public void SwapBuffers() => SDL_GL_SwapWindow(_window);

    public void Dispose()
    {
        Gl.Dispose();
        if (SdlContext != IntPtr.Zero)
            SDL_GL_DestroyContext((SDL_GLContextState*)SdlContext);
    }
}
