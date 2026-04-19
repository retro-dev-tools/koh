using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.App;

/// <summary>
/// Background-thread emulator pacer. The loop runs <c>RunFrame()</c> as
/// fast as the <see cref="AudioSink"/> can absorb samples: once the
/// sink reports enough buffered audio, we sleep; once it drains below
/// the low-water mark, we catch up. This keeps the emulated speed
/// exactly aligned to audio hardware clock (no drift, no monitor-refresh
/// coupling) so a 60 Hz display showing a 59.73 Hz Game Boy still
/// produces pitch-correct audio.
///
/// <para>
/// Ported from <c>feature/audio-driven-pacing</c> — the algorithm is
/// unchanged, but the surface is trimmed for the KohUI host: no
/// <c>IAudioSink</c> indirection (only one implementation), no
/// <c>FrameCompleted</c> event (the GL painter reads the live
/// framebuffer directly), and a simpler command mailbox because we
/// only need Pause/Resume/Quit in v1.
/// </para>
/// </summary>
public sealed class EmulatorLoop : IDisposable
{
    // Pacing targets in samples @ 44.1 kHz.
    // HighWater: above this, sleep and wait for the audio hardware to
    // drain. TargetFill: when resuming after HighWater, stop sleeping
    // once we're back to here. LowWater: below this, don't sleep at
    // all — we're at risk of an audible underrun.
    private const int HighWater  = 3072;   // ~70 ms
    private const int TargetFill = 2048;   // ~46 ms
    private const int LowWater   = 1024;   // ~23 ms

    private readonly AudioSink _sink;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _runGate = new(false);
    private readonly ManualResetEventSlim _exited = new(false);

    private int _command = (int)Command.None;
    private GameBoySystem? _system;
    private short[] _drainScratch = new short[2048];
    private volatile bool _disposed;
    private volatile bool _paused = true;

    // Inbound command/action queue. Drained at the top of each iteration
    // so handlers see a consistent GameBoySystem (not mid-RunFrame).
    // Used for joypad edges and anything else that can't race with the
    // emulator core — a concurrent Press on the Joypad struct or an
    // Interrupts.Raise from another thread would otherwise tear state.
    private readonly ConcurrentQueue<Action<GameBoySystem>> _inbox = new();

    // Published by the loop after each frame so the UI can render a
    // coherent snapshot without reaching into the live GameBoySystem.
    // Reference-stable: the PPU mutates the same byte[] in place, this
    // field just points at whichever buffer is current after the most
    // recent Flip().
    private byte[] _publishedFrame = s_emptyFrame;

    // Live frame counter for UI status bars. Written only by the loop
    // thread, read from anywhere — volatile for publish semantics.
    private long _frameCount;
    public long FrameCount => Volatile.Read(ref _frameCount);
    public bool IsPaused => _paused;

    // Per-frame CPU snapshot for the debug UI. Published after every
    // RunFrame on the loop thread; readers get a consistent snapshot
    // with a single volatile reference read. `null` until the first
    // ROM has booted.
    private CpuSnapshot? _cpuSnapshot;
    public CpuSnapshot? CurrentCpu => Volatile.Read(ref _cpuSnapshot);

    private PaletteSnapshot? _paletteSnapshot;
    public PaletteSnapshot? CurrentPalettes => Volatile.Read(ref _paletteSnapshot);

    /// <summary>
    /// Latest front framebuffer produced by the emulator, or an empty
    /// grey buffer before the first ROM boots. Reference is
    /// thread-stable; the PPU writes into the same backing array each
    /// frame, so readers get a live view.
    /// </summary>
    public byte[] CurrentFramebuffer => Volatile.Read(ref _publishedFrame);

    public EmulatorLoop(AudioSink sink)
    {
        _sink = sink;
        // Raise Windows timer resolution so Thread.Sleep(1) actually
        // sleeps ~1 ms instead of the default ~15 ms quantum. Our pacer
        // overshoot is proportional to Sleep granularity: with 15 ms
        // sleeps, the sink depth can overshoot TargetFill by ~660
        // samples per Sleep call, and any OS scheduling jitter past
        // that is enough to underrun. 1 ms resolution keeps the
        // overshoot < 50 samples. Corresponding timeEndPeriod runs in
        // Dispose — unbalanced calls would leave the whole process at
        // 1 ms resolution (mild battery impact on laptops).
        if (OperatingSystem.IsWindows())
        {
            _ = TimeBeginPeriod(1);
            _timerResolutionRaised = true;
        }
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "koh-emu-loop",
        };
        _thread.Start();
    }

    private readonly bool _timerResolutionRaised;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    /// <summary>
    /// Install (or replace) the <see cref="GameBoySystem"/> the loop
    /// operates on. Caller is expected to pause first; the loop holds
    /// a reference for the thread, so swapping mid-run would race.
    /// </summary>
    public void SetSystem(GameBoySystem? system)
    {
        _system = system;
        _sink.Reset();
        Volatile.Write(ref _frameCount, 0);
    }

    public void Pause()
    {
        Post(Command.Pause);
        _runGate.Reset();
        _paused = true;
    }

    public void Resume()
    {
        if (_system is null) return;
        Post(Command.Resume);
        _paused = false;
        _runGate.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Post(Command.Quit);
        _runGate.Set();
        _exited.Wait(TimeSpan.FromSeconds(1));
        if (_timerResolutionRaised && OperatingSystem.IsWindows())
            _ = TimeEndPeriod(1);
    }

    /// <summary>
    /// Queue an action to run on the loop thread between frames. Used
    /// for joypad edges and other state pokes that can't safely race
    /// with <c>RunFrame</c> on another thread. Action runs with the
    /// live <see cref="GameBoySystem"/> as its argument.
    /// </summary>
    public void Send(Action<GameBoySystem> action) => _inbox.Enqueue(action);

    private void Post(Command cmd) => Interlocked.Exchange(ref _command, (int)cmd);

    private void Run()
    {
        int lastBufferedAfterPush = 0;
        long lastPushTimestampTicks = 0;
        long lastLogTicks = Stopwatch.GetTimestamp();
        long lastLogFrames = 0;
        int lastLogUnderruns = 0;
        int lastLogPushes = 0;
        int lastLogSamples = 0;
        // Minimum / maximum buffer depth seen across the reporting window.
        // Useful for spotting "buffer briefly emptied" events even when
        // the post-push depth looks healthy.
        int windowMinDepth = int.MaxValue;
        int windowMaxDepth = 0;

        try
        {
            while (!_disposed)
            {
                long sinceLog = Stopwatch.GetTimestamp() - lastLogTicks;
                if (sinceLog > Stopwatch.Frequency)
                {
                    long now = Interlocked.Read(ref _frameCount);
                    int urNow = _sink.Underruns;
                    int pushesNow = _sink.TotalPushes;
                    int samplesNow = _sink.TotalSamplesIn;
                    double fps = (now - lastLogFrames) * (double)Stopwatch.Frequency / sinceLog;
                    int newUnderruns = urNow - lastLogUnderruns;
                    int windowPushes = pushesNow - lastLogPushes;
                    int windowSamples = samplesNow - lastLogSamples;
                    Console.WriteLine(
                        $"[pace] fps={fps:F1}  buf[last={lastBufferedAfterPush} min={windowMinDepth} max={windowMaxDepth}]  "
                        + $"pushes={windowPushes} samples={windowSamples} underruns={newUnderruns}");
                    lastLogFrames = now;
                    lastLogTicks = Stopwatch.GetTimestamp();
                    lastLogUnderruns = urNow;
                    lastLogPushes = pushesNow;
                    lastLogSamples = samplesNow;
                    windowMinDepth = int.MaxValue;
                    windowMaxDepth = 0;
                }
                if (_paused || _system is null)
                {
                    _runGate.Wait();
                    continue;
                }

                var cmd = (Command)Interlocked.Exchange(ref _command, (int)Command.None);
                if (cmd == Command.Pause) { _paused = true; _runGate.Reset(); continue; }
                if (cmd == Command.Quit)  return;

                var sys = _system;
                if (sys is null) continue;

                // Drain inbox BEFORE RunFrame — joypad edges applied
                // here land on the frame that's about to execute, so
                // there's minimal input lag.
                while (_inbox.TryDequeue(out var action))
                {
                    try { action(sys); }
                    catch (Exception ex) { Console.Error.WriteLine($"[koh-emu-loop] inbox action threw: {ex.Message}"); }
                }

                var stop = sys.RunFrame();
                Volatile.Write(ref _publishedFrame, sys.Ppu.Framebuffer.FrontArray);
                Volatile.Write(ref _cpuSnapshot, CpuSnapshot.From(sys));
                Volatile.Write(ref _paletteSnapshot, PaletteSnapshot.From(sys));
                Interlocked.Increment(ref _frameCount);

                int available = sys.Apu.SampleBuffer.Available;
                if (available > 0)
                {
                    if (_drainScratch.Length < available) _drainScratch = new short[available];
                    int n = sys.Apu.SampleBuffer.Drain(_drainScratch.AsSpan(0, available));
                    lastBufferedAfterPush = _sink.Push(_drainScratch.AsSpan(0, n));
                    lastPushTimestampTicks = Stopwatch.GetTimestamp();
                    if (lastBufferedAfterPush < windowMinDepth) windowMinDepth = lastBufferedAfterPush;
                    if (lastBufferedAfterPush > windowMaxDepth) windowMaxDepth = lastBufferedAfterPush;
                }

                if (stop.Reason is StopReason.Breakpoint or StopReason.Watchpoint)
                {
                    _paused = true;
                    _runGate.Reset();
                    continue;
                }

                // Pace by estimated buffer depth. Below HighWater we
                // burn CPU running the next frame — the emulator needs
                // headroom when the hardware hasn't accumulated too
                // many samples yet. A Thread.Sleep(0) "just in case"
                // yield looked harmless but costs ~15 ms on Windows
                // with default timer granularity, which is enough to
                // starve the sink during startup (fps drops to 47 and
                // we chew through the warmup buffer before audio
                // stabilises).
                if (lastBufferedAfterPush > HighWater)
                {
                    while (!_disposed && !_paused)
                    {
                        int est = EstimateBuffered(lastBufferedAfterPush, lastPushTimestampTicks);
                        if (est <= TargetFill) break;
                        Thread.Sleep(1);
                    }
                }
            }
        }
        finally
        {
            _exited.Set();
        }
    }

    /// <summary>
    /// Dead-reckoning estimate of "samples still queued at the sink"
    /// given the depth right after the last Push. The audio hardware
    /// drains at <see cref="AudioSink.SampleRate"/> samples/sec; so
    /// after <c>(now - tsPush)</c> seconds we've lost that many
    /// samples. Avoids calling <see cref="AudioSink.Buffered"/> in the
    /// tight sleep loop.
    /// </summary>
    private static int EstimateBuffered(int afterPush, long pushTicks)
    {
        double elapsedMs = (Stopwatch.GetTimestamp() - pushTicks) * 1000.0 / Stopwatch.Frequency;
        int drained = (int)(elapsedMs * (AudioSink.SampleRate / 1000.0));
        int remaining = afterPush - drained;
        return remaining < 0 ? 0 : remaining;
    }

    private enum Command
    {
        None = 0,
        Pause = 1,
        Resume = 2,
        Quit = 3,
    }

    private static readonly byte[] s_emptyFrame = BuildGreyFrame();

    private static byte[] BuildGreyFrame()
    {
        var buf = new byte[Framebuffer.Width * Framebuffer.Height * Framebuffer.BytesPerPixel];
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i + 0] = 0x2e;
            buf[i + 1] = 0x2e;
            buf[i + 2] = 0x2e;
            buf[i + 3] = 0xff;
        }
        return buf;
    }
}
