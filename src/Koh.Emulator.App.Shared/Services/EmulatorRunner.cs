using System.Diagnostics;
using System.Threading;
using Koh.Emulator.Core;

namespace Koh.Emulator.App.Services;

// Thread-based background runner. Primary target is the MAUI desktop host,
// where real threads are available. The browser host targets this library
// too (SupportedPlatform=browser) but will only work once
// WasmEnableThreads=true is enabled; that is an explicit non-goal of the
// audio-driven-pacing plan, so we suppress CA1416 here rather than adding
// an always-throwing browser fallback that would mask the migration gap.
#pragma warning disable CA1416 // Validate platform compatibility

/// <summary>
/// Background-thread host for the emulator loop. Audio-driven: after every
/// <c>RunFrame</c> we push samples to an <see cref="IAudioSink"/> and pace
/// based on the returned fill level.
/// </summary>
public sealed class EmulatorRunner : IDisposable
{
    // Pacing targets in samples @ 44.1 kHz.
    private const int HighWater  = 3072;   // ~70 ms
    private const int TargetFill = 2048;   // ~46 ms
    private const int LowWater   = 1024;   // ~23 ms

    private readonly IAudioSink _sink;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _runGate = new(false);
    private readonly ManualResetEventSlim _exited = new(false);

    // 1-slot command mailbox. Atomically overwrite with the newest command.
    private int _command = (int)RunnerCommand.None;

    private GameBoySystem? _system;
    private short[] _drainScratch = new short[2048];
    private volatile bool _disposed;
    private volatile bool _paused = true;

    public EmulatorRunner(IAudioSink sink)
    {
        _sink = sink;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "koh-emu-runner",
        };
        _thread.Start();
    }

    public IAudioSink Sink => _sink;
    public bool IsPaused => _paused;

    /// <summary>
    /// Install (or replace) the <see cref="GameBoySystem"/> the runner
    /// operates on. Must only be called while paused.
    /// </summary>
    public void SetSystem(GameBoySystem? system)
    {
        _system = system;
        _sink.Reset();
    }

    public event Action? StateChanged;
    public event Action<Exception>? FatalError;
    public event Action? FrameCompleted;

    public void Pause()
    {
        Post(RunnerCommand.Pause);
        _runGate.Reset();
        _paused = true;
        StateChanged?.Invoke();
    }

    public void Resume()
    {
        if (_system is null) return;
        Post(RunnerCommand.Resume);
        _paused = false;
        _runGate.Set();
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Post(RunnerCommand.Quit);
        _runGate.Set();       // unpark if sleeping
        _exited.Wait(TimeSpan.FromSeconds(1));
    }

    private void Post(RunnerCommand cmd) => Interlocked.Exchange(ref _command, (int)cmd);

    private void Loop()
    {
        long lastBufferedTimestampTicks = 0;
        int lastBufferedAfter = 0;

        try
        {
            while (!_disposed)
            {
                if (_paused || _system is null)
                {
                    _runGate.Wait();
                    continue;
                }

                var cmd = (RunnerCommand)Interlocked.Exchange(ref _command, (int)RunnerCommand.None);
                switch (cmd)
                {
                    case RunnerCommand.Pause:
                        _paused = true;
                        _runGate.Reset();
                        continue;
                    case RunnerCommand.Quit:
                        return;
                }

                var sys = _system;
                if (sys is null) continue;

                var stop = sys.RunFrame();

                int available = sys.Apu.SampleBuffer.Available;
                if (available > 0)
                {
                    if (_drainScratch.Length < available) _drainScratch = new short[available];
                    int n = sys.Apu.SampleBuffer.Drain(_drainScratch.AsSpan(0, available));
                    lastBufferedAfter = _sink.Push(_drainScratch.AsSpan(0, n));
                    lastBufferedTimestampTicks = Stopwatch.GetTimestamp();
                }

                FrameCompleted?.Invoke();

                if (stop.Reason == StopReason.Breakpoint || stop.Reason == StopReason.Watchpoint)
                {
                    _paused = true;
                    _runGate.Reset();
                    StateChanged?.Invoke();
                    continue;
                }

                if (lastBufferedAfter > HighWater)
                {
                    while (!_disposed && !_paused)
                    {
                        int est = FastEstimateBuffered(lastBufferedAfter, lastBufferedTimestampTicks);
                        if (est <= TargetFill) break;
                        Thread.Sleep(1);
                    }
                }
                else if (lastBufferedAfter > LowWater)
                {
                    Thread.Sleep(0);
                }
                // else: starving → loop immediately, no sleep
            }
        }
        catch (Exception ex)
        {
            _paused = true;
            FatalError?.Invoke(ex);
            StateChanged?.Invoke();
        }
        finally
        {
            _exited.Set();
        }
    }

    private static int FastEstimateBuffered(int bufferedAfterPush, long tsPush)
    {
        double elapsedMs = (Stopwatch.GetTimestamp() - tsPush) * 1000.0 / Stopwatch.Frequency;
        int drained = (int)(elapsedMs * 44.1);
        return Math.Max(0, bufferedAfterPush - drained);
    }

    private enum RunnerCommand
    {
        None = 0,
        Pause = 1,
        Resume = 2,
        Quit = 3,
    }
}
