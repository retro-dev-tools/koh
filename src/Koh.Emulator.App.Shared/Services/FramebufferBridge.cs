using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Bridges a framebuffer byte[] to the HTML canvas via JS interop.
///
/// Commit is fire-and-forget async: Blazor marshals the byte[] synchronously
/// before <see cref="IJSRuntime.InvokeVoidAsync(string, object?[])"/> returns
/// the task, so we can drop the returned task on the floor without racing
/// the caller who immediately releases the front buffer. We cannot block
/// on the task: in MAUI Blazor Hybrid the JS runtime is NOT an
/// <c>IJSInProcessRuntime</c>, so a sync-over-async wait on the dispatcher
/// thread deadlocks the UI.
/// </summary>
public sealed class FramebufferBridge
{
    private readonly IJSRuntime _js;
    private DotNetObjectReference<FramebufferBridge>? _rafRef;
    private Action? _onRaf;

    public FramebufferBridge(IJSRuntime js) { _js = js; }

    public ValueTask AttachAsync(string canvasId)
        => _js.InvokeVoidAsync("kohFramebufferBridge.attach", canvasId);

    public void Commit(byte[] frame)
    {
        // Fire-and-forget: arguments are marshalled synchronously.
        _ = _js.InvokeVoidAsync("kohFramebufferBridge.commit", frame);
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
