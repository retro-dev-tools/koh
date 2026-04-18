using System.Runtime.InteropServices;
using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

/// <summary>
/// <see cref="IAudioSink"/> over a Blazor <see cref="IJSRuntime"/> bridge.
///
/// Push marshals samples into a reusable <c>byte[]</c> (Int16 little-
/// endian) and calls <c>kohAudio.pushSamples(bytes) -> int bufferedAfter</c>
/// via the synchronous in-process runtime when available. If the
/// runtime isn't in-process, falls back to
/// <c>.InvokeAsync&lt;int&gt;(...).AsTask().GetAwaiter().GetResult()</c> —
/// slower but functionally identical.
///
/// Stats: <see cref="Buffered"/> is returned from every push; the JS side
/// periodically posts cumulative underrun/overrun counters back via
/// <see cref="UpdateCounters"/>.
/// </summary>
public sealed class AudioPipe : IAudioSink, IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly IJSInProcessRuntime? _jsSync;
    private byte[] _scratch = Array.Empty<byte>();
    private bool _initialized;
    private AudioIsolationLevel _level = AudioIsolationLevel.Muted;
    private long _underruns;
    private long _overruns;
    private int _buffered;
    private DotNetObjectReference<AudioPipe>? _selfRef;

    public AudioPipe(IJSRuntime js)
    {
        _js = js;
        _jsSync = js as IJSInProcessRuntime;
    }

    public AudioIsolationLevel IsolationLevel => _level;
    public int Buffered => _buffered;
    public long Underruns => System.Threading.Interlocked.Read(ref _underruns);
    public long Overruns  => System.Threading.Interlocked.Read(ref _overruns);

    public async ValueTask InitAsync(int sampleRate = 44_100)
    {
        if (_initialized) return;
        _selfRef = DotNetObjectReference.Create(this);
        var result = await _js.InvokeAsync<string>("kohAudio.init", sampleRate, _selfRef);
        _level = result switch
        {
            "worklet" => AudioIsolationLevel.Worklet,
            "degraded" => AudioIsolationLevel.Degraded,
            _ => AudioIsolationLevel.Muted,
        };
        _initialized = true;
    }

    public int Push(ReadOnlySpan<short> samples)
    {
        if (!_initialized || samples.IsEmpty) return _buffered;

        int byteLen = samples.Length * 2;
        if (_scratch.Length < byteLen) _scratch = new byte[byteLen];
        MemoryMarshal.AsBytes(samples).CopyTo(_scratch);
        var span = _scratch.AsMemory(0, byteLen);

        int bufferedAfter;
        if (_jsSync is not null)
            bufferedAfter = _jsSync.Invoke<int>("kohAudio.pushSamples", span);
        else
            bufferedAfter = _js.InvokeAsync<int>("kohAudio.pushSamples", span).AsTask().GetAwaiter().GetResult();

        _buffered = bufferedAfter;
        return bufferedAfter;
    }

    public void Reset()
    {
        if (!_initialized) return;
        _buffered = 0;
        if (_jsSync is not null) _jsSync.InvokeVoid("kohAudio.reset");
        else _js.InvokeVoidAsync("kohAudio.reset").AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Called from JS ~4 times per second with cumulative underrun/overrun
    /// counts. We store the latest.
    /// </summary>
    [JSInvokable]
    public void UpdateCounters(long underruns, long overruns)
    {
        System.Threading.Interlocked.Exchange(ref _underruns, underruns);
        System.Threading.Interlocked.Exchange(ref _overruns, overruns);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_initialized) return;
        try { await _js.InvokeVoidAsync("kohAudio.shutdown"); }
        catch { /* webview may be torn down */ }
        _selfRef?.Dispose();
        _initialized = false;
    }
}
