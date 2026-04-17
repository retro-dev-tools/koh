using Microsoft.JSInterop;
using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Bridges the emulator framebuffer to the HTML canvas via JS interop.
/// Ships the raw RGBA bytes directly — Blazor's JS interop marshals byte[]
/// efficiently (no base64), which matters at 60 fps: the old path produced
/// ~5 MB/s of UTF-16 string traffic and was visibly stealing frame budget.
/// </summary>
public sealed class FramebufferBridge
{
    private readonly IJSRuntime _js;

    public FramebufferBridge(IJSRuntime js) { _js = js; }

    public ValueTask AttachAsync(string canvasId)
        => _js.InvokeVoidAsync("kohFramebufferBridge.attach", canvasId);

    public ValueTask CommitAsync(Framebuffer framebuffer)
    {
        // Blazor marshals byte[] as Uint8Array on the JS side. The old
        // implementation here base64-encoded the buffer in .NET, shipped it
        // as a string, and decoded with atob on the JS side — ~5 MB/s of
        // UTF-16 traffic + a big allocation every frame.
        return _js.InvokeVoidAsync("kohFramebufferBridge.commit", framebuffer.FrontArray);
    }
}
