using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Bridges a framebuffer <c>byte[]</c> to the HTML canvas via JS interop.
/// Commit is synchronous so the rAF loop in JS can pull a fresh front
/// buffer from <see cref="FramePublisher"/> each tick without awaiting a
/// Blazor Task round-trip.
/// </summary>
public sealed class FramebufferBridge
{
    private readonly IJSRuntime _js;
    private readonly IJSInProcessRuntime? _jsSync;
    private DotNetObjectReference<FramebufferBridge>? _rafRef;
    private Action? _onRaf;

    public FramebufferBridge(IJSRuntime js)
    {
        _js = js;
        _jsSync = js as IJSInProcessRuntime;
    }

    public ValueTask AttachAsync(string canvasId)
        => _js.InvokeVoidAsync("kohFramebufferBridge.attach", canvasId);

    public void CommitSync(byte[] frame)
    {
        if (_jsSync is not null)
            _jsSync.InvokeVoid("kohFramebufferBridge.commit", frame);
        else
            _js.InvokeVoidAsync("kohFramebufferBridge.commit", frame).AsTask().GetAwaiter().GetResult();
    }

    public ValueTask StartRafLoopAsync(Action onFrame)
    {
        _onRaf = onFrame;
        _rafRef = DotNetObjectReference.Create(this);
        return _js.InvokeVoidAsync("kohFramebufferBridge.startRafLoop", _rafRef);
    }

    public ValueTask StopRafLoopAsync()
    {
        _onRaf = null;
        var t = _js.InvokeVoidAsync("kohFramebufferBridge.stopRafLoop");
        _rafRef?.Dispose();
        _rafRef = null;
        return t;
    }

    [JSInvokable]
    public void OnRaf() => _onRaf?.Invoke();
}
