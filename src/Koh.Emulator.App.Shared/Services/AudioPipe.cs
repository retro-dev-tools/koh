using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.JSInterop;

namespace Koh.Emulator.App.Services;

/// <summary>
/// <see cref="IAudioSink"/> over a Blazor <see cref="IJSRuntime"/> bridge.
///
/// Push is called from the runner's background thread. In MAUI Blazor
/// Hybrid the JS runtime is async-only (no <c>IJSInProcessRuntime</c>) and
/// sync-over-async on the dispatcher thread deadlocks the UI, so we can't
/// block on the JS response. Instead we fire the <c>pushSamples</c> call
/// as async-and-forget — Blazor marshals the byte array synchronously
/// during the call, the returned task completes asynchronously, and the
/// worklet reports the device-side fill level back via
/// <see cref="UpdateCounters"/> ~4 Hz. Pacing uses that cached counter.
/// </summary>
public sealed class AudioPipe : IAudioSink, IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private bool _initialized;
    private AudioIsolationLevel _level = AudioIsolationLevel.Muted;
    private long _underruns;
    private long _overruns;
    private int _buffered;
    private DotNetObjectReference<AudioPipe>? _selfRef;

    // Counts how many samples we've shipped to JS that the worklet has not
    // yet reported as "consumed" via UpdateCounters. Used as a fresher
    // estimate of the device-side fill than the stale ~4 Hz callback.
    private long _pushedSinceCounter;

    public AudioPipe(IJSRuntime js)
    {
        _js = js;
    }

    public AudioIsolationLevel IsolationLevel => _level;
    public int Buffered => _buffered + (int)Interlocked.Read(ref _pushedSinceCounter);
    public long Underruns => Interlocked.Read(ref _underruns);
    public long Overruns  => Interlocked.Read(ref _overruns);

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
        if (!_initialized || samples.IsEmpty) return Buffered;

        int byteLen = samples.Length * 2;
        // Allocate fresh — ownership passes to the Blazor marshaller,
        // which serialises synchronously; reusing a scratch buffer here
        // would race the in-flight serialisation on the next push.
        var bytes = new byte[byteLen];
        MemoryMarshal.AsBytes(samples).CopyTo(bytes);
        _ = _js.InvokeVoidAsync("kohAudio.pushSamples", bytes);

        Interlocked.Add(ref _pushedSinceCounter, samples.Length);
        return Buffered;
    }

    public void Reset()
    {
        if (!_initialized) return;
        _buffered = 0;
        Interlocked.Exchange(ref _pushedSinceCounter, 0);
        _ = _js.InvokeVoidAsync("kohAudio.reset");
    }

    /// <summary>
    /// Called from JS ~4 Hz with the worklet's cumulative underrun/overrun
    /// counts and its instantaneous fill level (write-read gap).
    /// </summary>
    [JSInvokable]
    public void UpdateCounters(long underruns, long overruns, int buffered)
    {
        Interlocked.Exchange(ref _underruns, underruns);
        Interlocked.Exchange(ref _overruns, overruns);
        Volatile.Write(ref _buffered, buffered);
        // The JS-side count is authoritative; reset the "in-flight" estimate.
        Interlocked.Exchange(ref _pushedSinceCounter, 0);
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
