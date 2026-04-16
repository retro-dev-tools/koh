using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

public sealed class WebAudioBridge : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private bool _initialized;

    public WebAudioBridge(IJSRuntime js) { _js = js; }

    public async ValueTask InitAsync(int sampleRate = 44100)
    {
        if (_initialized) return;
        await _js.InvokeVoidAsync("kohWebAudio.init", sampleRate);
        _initialized = true;
    }

    public async ValueTask PushAsync(ReadOnlyMemory<short> samples)
    {
        if (!_initialized || samples.Length == 0) return;
        var floats = new float[samples.Length];
        var span = samples.Span;
        for (int i = 0; i < floats.Length; i++)
            floats[i] = span[i] / 32768f;

        await _js.InvokeVoidAsync("kohWebAudio.pushSamples", floats);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_initialized) return;
        try { await _js.InvokeVoidAsync("kohWebAudio.shutdown"); }
        catch { /* runtime may be torn down already */ }
        _initialized = false;
    }
}
