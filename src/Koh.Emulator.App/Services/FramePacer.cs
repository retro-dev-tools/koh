using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

public sealed class FramePacer
{
    private readonly IJSRuntime _js;
    public FramePacer(IJSRuntime js) { _js = js; }

    public async ValueTask WaitForNextFrameAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("kohFramePacer.waitForRaf");
        }
        catch
        {
            // Fallback: yield to the event loop.
            await Task.Yield();
        }
    }
}
