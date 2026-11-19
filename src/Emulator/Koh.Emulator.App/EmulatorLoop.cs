using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Koh.Emulator;
using Koh.Emulator.Ppu;

namespace Koh.Emulator.App;

/// <summary>
/// Emulator pacing with optional audio.
/// </summary>
public sealed class EmulatorLoop : IDisposable
{
    // Pacing targets in samples @ 44.1 kHz.
    // HighWater: above this, sleep and wait for the audio hardware to
    // drain. TargetFill: when resuming after HighWater, stop sleeping
    // once we're back to here. LowWater: below this, don't sleep at
    // all — we're at risk of an audible underrun.
    private const int HighWater = 3072; // ~70 ms
    private const int TargetFill = 2048; // ~46 ms
    private const int LowWater = 1024; // ~23 ms

    private readonly AudioSink? _sink;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private Exception? _failure;

    private GameBoySystem? _system;
    private short[] _drainScratch = new short[2048];
    private volatile bool _disposed;
    private volatile bool _paused = true;

    // Frame-boundary commands
    private readonly ConcurrentQueue<Action> _inbox = new();
    private readonly object _submissionLock = new();

    // Path of the ROM backing the current _system, if any. Retained so
    // we can write the battery-backed SRAM to "<rom>.sav" on ROM swap
    // and app exit without reaching back through the model.
    private string? _currentRomPath;

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

    /// <summary>
    /// The live <see cref="GameBoySystem"/> the loop is driving, or
    /// null if no ROM is loaded. Safe to read from any thread; only
    /// safe to MUTATE while the loop is paused (peek at registers,
    /// install breakpoints, read memory) — anything else races the
    /// emulator thread's RunFrame.
    /// </summary>
    public GameBoySystem? CurrentSystem => _system;

    // Hold-to-run-uncapped fast-forward. Set from the UI thread while
    // the user holds the fast-forward key; cleared on release. While
    // true the pacer skips its HighWater sleep — the emulator runs as
    // fast as the CPU allows. Audio keeps being pushed but samples
    // beyond the sink's capacity are dropped (OpenAL's free-buffer
    // pool is small), so playback simply chops into near-silence
    // until Tab is released and the pacer catches up.
    private volatile bool _fastForward;
    public bool FastForward
    {
        get => _fastForward;
        set => _fastForward = value;
    }

    /// <summary>
    /// Latest front framebuffer produced by the emulator, or an empty
    /// grey buffer before the first ROM boots. Reference is
    /// thread-stable; the PPU writes into the same backing array each
    /// frame, so readers get a live view.
    /// </summary>
    public byte[] CurrentFramebuffer => Volatile.Read(ref _publishedFrame);

    // Emulator thread
    public event Action<GameBoySystem>? SystemInstalled;

    /// <summary>
    /// Fires on the emulator thread when a frame ends with a
    /// breakpoint or watchpoint. The loop itself already flipped
    /// itself to paused by the time this fires; listeners typically
    /// notify external debuggers (DAP "stopped" event) or update
    /// status UI.
    /// </summary>
    public event Action<StopReason>? PausedOnBreak;

    public EmulatorLoop(AudioSink? sink = null)
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
            // AboveNormal so a background GC thread or an unrelated
            // Task.Run pool worker can't preempt us at the wrong ms
            // and starve the sink. The emulator still yields on the
            // HighWater sleep, so this doesn't peg a core.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private readonly bool _timerResolutionRaised;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    public void SetSystem(GameBoySystem? system, string? romPath = null) =>
        Execute(() => InstallSystem(system, romPath));

    private void InstallSystem(GameBoySystem? system, string? romPath)
    {
        if (_system is not null && _currentRomPath is not null)
            SaveSramNow(_system, _currentRomPath);
        if (system is not null && romPath is not null)
            LoadSramInto(system, romPath);
        _system = system;
        _currentRomPath = romPath;
        _sink?.Reset();
        Volatile.Write(ref _frameCount, 0);
        if (system is not null)
        {
            PublishFrame(system);
            SystemInstalled?.Invoke(system);
        }
    }

    /// <summary>
    /// Atomically persist the battery-backed SRAM. The write lands as
    /// <c>&lt;romPath&gt;.tmp</c> first, then renames over the final
    /// <c>.sav</c> — a crash mid-write can leave the temp file behind
    /// but never a half-written save.
    /// </summary>
    private static void SaveSramNow(GameBoySystem sys, string romPath)
    {
        var ram = sys.Cartridge.Ram;
        if (ram.Length == 0)
            return;
        string savPath = romPath + ".sav";
        string tmp = savPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, ram);
            // Single-syscall rename: atomic on POSIX (rename(2)); on
            // Windows (MoveFileEx + MOVEFILE_REPLACE_EXISTING) it
            // eliminates the user-visible missing-file window that a
            // Delete + Move pair would leave on crash/power-loss.
            File.Move(tmp, savPath, overwrite: true);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            { /* best-effort */
            }
            throw new IOException($"SRAM save failed ({savPath}): {ex.Message}", ex);
        }
    }

    private static void LoadSramInto(GameBoySystem sys, string romPath)
    {
        var ram = sys.Cartridge.Ram;
        if (ram.Length == 0)
            return;
        string savPath = romPath + ".sav";
        if (!File.Exists(savPath))
            return;
        try
        {
            var bytes = File.ReadAllBytes(savPath);
            // Tolerate size mismatches: copy what fits, zero-fill if
            // the .sav is shorter (SRAM was already zero-initialised
            // at cart construction, so a short read leaves the tail
            // untouched).
            int copy = Math.Min(bytes.Length, ram.Length);
            Buffer.BlockCopy(bytes, 0, ram, 0, copy);
        }
        catch (Exception ex)
        {
            throw new IOException($"SRAM load failed ({savPath}): {ex.Message}", ex);
        }
    }

    public void Pause() => Execute(() => _paused = true);

    public void Resume() => Execute(() => _paused = _system is null);

    public void Dispose()
    {
        lock (_submissionLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _wake.Set();
        }
        _thread.Join();
        // Flush the last SRAM after the loop exits so we're not racing
        // the background thread's final RunFrame (which may have
        // written fresh bytes into cart.Ram just before Quit).
        try
        {
            if (_system is not null && _currentRomPath is not null)
                SaveSramNow(_system, _currentRomPath);
        }
        finally
        {
            _wake.Dispose();
            if (_timerResolutionRaised && OperatingSystem.IsWindows())
                _ = TimeEndPeriod(1);
        }
    }

    // Synchronous frame-boundary mutation
    public void Send(Action<GameBoySystem> action) =>
        Execute(() =>
        {
            if (_system is { } system)
                action(system);
        });

    public void Execute(Action action) =>
        Execute(() =>
        {
            action();
            return true;
        });

    public T Execute<T>(Func<T> action)
    {
        if (Thread.CurrentThread == _thread)
            return action();
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        lock (_submissionLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failure is not null)
                throw new InvalidOperationException("Emulator loop stopped", _failure);
            _inbox.Enqueue(() =>
            {
                try
                {
                    if (_failure is not null)
                        throw new InvalidOperationException("Emulator loop stopped", _failure);
                    completion.SetResult(action());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
            _wake.Set();
        }
        return completion.Task.GetAwaiter().GetResult();
    }

    public void StepOne() =>
        Execute(() =>
        {
            if (_system is null || !_paused)
                return;
            _system.StepInstruction();
            PublishFrame(_system);
            PausedOnBreak?.Invoke(StopReason.InstructionComplete);
        });

    /// <summary>
    /// Serialise the full System state to <paramref name="path"/>.
    /// Runs on the emulator thread between frames; the file is
    /// complete-or-absent (written atomically via temp + rename) so a
    /// crash mid-write can't leave a half-written save.
    /// </summary>
    public void SaveState(string path) =>
        Send(sys =>
        {
            string tmp = path + ".tmp";
            try
            {
                using (var fs = File.Create(tmp))
                using (var w = new Emulator.State.StateWriter(fs))
                {
                    sys.WriteState(w);
                }
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch
                { /* best-effort */
                }
                throw new IOException($"Save state failed: {ex.Message}", ex);
            }
        });

    public void LoadState(string path) =>
        Send(sys =>
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("No save state", path);
            using var rollback = new MemoryStream();
            using (var writer = new Emulator.State.StateWriter(rollback))
                sys.WriteState(writer);
            try
            {
                using var fs = File.OpenRead(path);
                using var r = new Emulator.State.StateReader(fs);
                sys.ReadState(r);
                // Audio queued before the load doesn't belong to the
                // restored world — drop it so the new state's first
                // samples aren't preceded by stale tail.
                _sink?.Reset();
                PublishFrame(sys);
            }
            catch
            {
                rollback.Position = 0;
                using var reader = new Emulator.State.StateReader(rollback);
                sys.ReadState(reader);
                throw;
            }
        });

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
            while (true)
            {
                while (_inbox.TryDequeue(out var action))
                    action();
                if (_disposed)
                    break;
                long sinceLog = Stopwatch.GetTimestamp() - lastLogTicks;
                if (_sink is not null && sinceLog > Stopwatch.Frequency)
                {
                    long now = Interlocked.Read(ref _frameCount);
                    int urNow = _sink.Underruns;
                    int pushesNow = _sink.TotalPushes;
                    int samplesNow = _sink.TotalSamplesIn;
                    double fps = (now - lastLogFrames) * (double)Stopwatch.Frequency / sinceLog;
                    int newUnderruns = urNow - lastLogUnderruns;
                    int windowPushes = pushesNow - lastLogPushes;
                    int windowSamples = samplesNow - lastLogSamples;
                    Console.Error.WriteLine(
                        $"[pace] fps={fps:F1}  buf[last={lastBufferedAfterPush} min={windowMinDepth} max={windowMaxDepth}]  "
                            + $"pushes={windowPushes} samples={windowSamples} underruns={newUnderruns}"
                    );
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
                    _wake.WaitOne();
                    continue;
                }

                var sys = _system;
                if (sys is null)
                    continue;

                long frameStarted = Stopwatch.GetTimestamp();
                var stop = sys.RunFrame();
                PublishFrame(sys);
                Interlocked.Increment(ref _frameCount);

                int available = sys.Apu.SampleBuffer.Available;
                if (available > 0)
                {
                    if (_drainScratch.Length < available)
                        _drainScratch = new short[available];
                    int n = sys.Apu.SampleBuffer.Drain(_drainScratch.AsSpan(0, available));
                    lastBufferedAfterPush = _sink?.Push(_drainScratch.AsSpan(0, n)) ?? 0;
                    lastPushTimestampTicks = Stopwatch.GetTimestamp();
                    if (lastBufferedAfterPush < windowMinDepth)
                        windowMinDepth = lastBufferedAfterPush;
                    if (lastBufferedAfterPush > windowMaxDepth)
                        windowMaxDepth = lastBufferedAfterPush;
                }

                if (stop.Reason is StopReason.Breakpoint or StopReason.Watchpoint)
                {
                    _paused = true;
                    PausedOnBreak?.Invoke(stop.Reason);
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
                if (_sink is null)
                {
                    double frameTicks = Stopwatch.Frequency * (70224.0 / 4194304);
                    while (
                        !_disposed
                        && !_paused
                        && !_fastForward
                        && Stopwatch.GetTimestamp() - frameStarted < frameTicks
                    )
                        Thread.Sleep(1);
                }
                else if (lastBufferedAfterPush > HighWater && !_fastForward)
                {
                    while (!_disposed && !_paused && !_fastForward)
                    {
                        int est = EstimateBuffered(lastBufferedAfterPush, lastPushTimestampTicks);
                        if (est <= TargetFill)
                            break;
                        Thread.Sleep(1);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (_submissionLock)
            {
                _failure = ex;
                while (_inbox.TryDequeue(out var pending))
                    pending();
            }
            Console.Error.WriteLine($"[koh-emu-loop] {ex.Message}");
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

    private void PublishFrame(GameBoySystem system) =>
        Volatile.Write(ref _publishedFrame, system.Ppu.Framebuffer.FrontArray);

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
