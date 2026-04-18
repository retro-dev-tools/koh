namespace Koh.Emulator.App.Services;

/// <summary>Level of isolation the audio transport was able to negotiate.</summary>
public enum AudioIsolationLevel
{
    /// <summary>AudioWorklet on an SAB ring — best case, glitch-resistant.</summary>
    Worklet,
    /// <summary>AudioWorklet using <c>port.postMessage</c> transfers — works without COOP/COEP.</summary>
    Degraded,
    /// <summary>No audio output; producer still paces off a Stopwatch.</summary>
    Muted,
}

/// <summary>
/// Destination for audio samples. The runner calls <see cref="Push"/>
/// once per emulated frame with ~738 samples and expects a current
/// buffered-samples count back to drive its pacing loop.
/// </summary>
public interface IAudioSink
{
    AudioIsolationLevel IsolationLevel { get; }

    /// <summary>Samples currently buffered at the audio device (read-side).</summary>
    int Buffered { get; }

    /// <summary>Cumulative underruns reported by the device.</summary>
    long Underruns { get; }

    /// <summary>Cumulative overruns (samples dropped) reported by the device.</summary>
    long Overruns { get; }

    /// <summary>
    /// Push samples to the device. Returns the fill level immediately
    /// after the push — used by the pacing loop to decide whether to
    /// sleep, tight-loop, or yield.
    /// </summary>
    int Push(ReadOnlySpan<short> samples);

    /// <summary>Drop any buffered audio (on ROM load / save-state load / reset).</summary>
    void Reset();
}
