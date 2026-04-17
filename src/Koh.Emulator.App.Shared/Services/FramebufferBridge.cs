using Microsoft.JSInterop;
using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Bridges the emulator framebuffer to the HTML canvas via JS interop. Phase 2
/// uses a base64 single-copy path per §10.4; a later phase can swap to
/// IJSUnmarshalledRuntime for a zero-copy span transfer.
/// </summary>
public sealed class FramebufferBridge
{
    private readonly IJSRuntime _js;

    public FramebufferBridge(IJSRuntime js) { _js = js; }

    public ValueTask AttachAsync(string canvasId)
        => _js.InvokeVoidAsync("kohFramebufferBridge.attach", canvasId);

    public ValueTask CommitAsync(Framebuffer framebuffer)
    {
        var bytes = framebuffer.Front.ToArray();
        string base64 = Convert.ToBase64String(bytes);
        return _js.InvokeVoidAsync("kohFramebufferBridge.commit", base64);
    }
}
