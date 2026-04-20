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

    // Per-frame CPU snapshot for the debug UI. Published after every
    // RunFrame on the loop thread; readers get a consistent snapshot
    // with a single volatile reference read. `null` until the first
    // ROM has booted.
    private CpuSnapshot? _cpuSnapshot;
    public CpuSnapshot? CurrentCpu => Volatile.Read(ref _cpuSnapshot);

    private PaletteSnapshot? _paletteSnapshot;
    public PaletteSnapshot? CurrentPalettes => Volatile.Read(ref _paletteSnapshot);

    private VramSnapshot? _vramSnapshot;
    public VramSnapshot? CurrentVram => Volatile.Read(ref _vramSnapshot);

    private MemorySnapshot? _memorySnapshot;
    public MemorySnapshot? CurrentMemory => Volatile.Read(ref _memorySnapshot);

    // Sliding-window base address for the memory view. Written by
    // the runner thread (ScrollMemory handler); read by the loop
    // thread every frame at snapshot time.
    private int _memoryViewAddress;
    public ushort MemoryViewAddress
    {
        get => (ushort)Volatile.Read(ref _memoryViewAddress);
        set => Volatile.Write(ref _memoryViewAddress, value);
    }

    // Gate the expensive debug publishers (palette, VRAM) on whether
    // the UI is actually showing them. CPU snapshot is tiny so we
    // always publish it — covers the "Frame N" counter even when the
    // debug panel is hidden. Writing during frame N is fine; worst case
    // one frame of stale data.
    private volatile bool _publishDebugSnapshots;
    public bool PublishDebugSnapshots
    {
        get => _publishDebugSnapshots;
        set => _publishDebugSnapshots = value;
    }

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

    /// <summary>
    /// Fires on the runner / UI thread whenever <see cref="SetSystem"/>
    /// installs a new live <see cref="GameBoySystem"/>. Consumers that
    /// need to attach hooks (debugger's watchpoint + breakpoint
    /// checker, snapshot tap, etc.) subscribe here instead of racing
    /// the loop thread for the <c>_system</c> field.
    /// </summary>
    public event Action<GameBoySystem>? SystemInstalled;

    /// <summary>
    /// Fires on the emulator thread when a frame ends with a
    /// breakpoint or watchpoint. The loop itself already flipped
    /// itself to paused by the time this fires; listeners typically
    /// notify external debuggers (DAP "stopped" event) or update
    /// status UI.
    /// </summary>
    public event Action<StopReason>? PausedOnBreak;

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

    /// <summary>
    /// Install (or replace) the <see cref="GameBoySystem"/> the loop
    /// operates on. Caller is expected to pause first; the loop holds
    /// a reference for the thread, so swapping mid-run would race.
    ///
    /// <para>
    /// Writes the outgoing system's battery-backed SRAM to its
    /// <c>.sav</c> path before dropping the reference, and attempts to
    /// load the incoming ROM's <c>.sav</c> into the new system's
    /// cartridge RAM — so save data carries across ROM swaps and
    /// relaunches without the app having to orchestrate it.
    /// </para>
    /// </summary>
    public void SetSystem(GameBoySystem? system, string? romPath = null)
    {
        if (_system is not null && _currentRomPath is not null) SaveSramNow(_system, _currentRomPath);
        _system = system;
        _currentRomPath = romPath;
        if (system is not null && romPath is not null) LoadSramInto(system, romPath);
        _sink.Reset();
        Volatile.Write(ref _frameCount, 0);
        if (system is not null) SystemInstalled?.Invoke(system);
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
        if (ram.Length == 0) return;
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
            Console.Error.WriteLine($"[koh-emu-loop] SRAM save failed ({savPath}): {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    private static void LoadSramInto(GameBoySystem sys, string romPath)
    {
        var ram = sys.Cartridge.Ram;
        if (ram.Length == 0) return;
        string savPath = romPath + ".sav";
        if (!File.Exists(savPath)) return;
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
            Console.Error.WriteLine($"[koh-emu-loop] SRAM load failed ({savPath}): {ex.Message}");
        }
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
        // Flush the last SRAM after the loop exits so we're not racing
        // the background thread's final RunFrame (which may have
        // written fresh bytes into cart.Ram just before Quit).
        if (_system is not null && _currentRomPath is not null)
            SaveSramNow(_system, _currentRomPath);
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

    /// <summary>
    /// Single-step exactly one instruction. The loop must be paused
    /// (<see cref="IsPaused"/>); execution happens on the caller's
    /// thread because we're holding the run gate closed anyway. Fires
    /// <see cref="PausedOnBreak"/> with <see cref="StopReason.InstructionComplete"/>
    /// so listeners can distinguish step completion from a real
    /// breakpoint hit.
    /// </summary>
    public void StepOne()
    {
        if (_system is null || !_paused) return;
        _system.StepInstruction();
        PausedOnBreak?.Invoke(StopReason.InstructionComplete);
    }

    /// <summary>
    /// Serialise the full System state to <paramref name="path"/>.
    /// Runs on the emulator thread between frames; the file is
    /// complete-or-absent (written atomically via temp + rename) so a
    /// crash mid-write can't leave a half-written save.
    /// </summary>
    public void SaveState(string path) => _inbox.Enqueue(sys =>
    {
        string tmp = path + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var w = new Core.State.StateWriter(fs))
            {
                sys.WriteState(w);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[koh-emu-loop] save state failed: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        }
    });

    /// <summary>
    /// Restore a previously-saved state. Silently no-ops if the path
    /// doesn't exist so the hotkey doesn't spam errors when the user
    /// hits Load before they've ever saved.
    /// </summary>
    public void LoadState(string path) => _inbox.Enqueue(sys =>
    {
        if (!File.Exists(path)) return;
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new Core.State.StateReader(fs);
            sys.ReadState(r);
            // Audio queued before the load doesn't belong to the
            // restored world — drop it so the new state's first
            // samples aren't preceded by stale tail.
            _sink.Reset();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[koh-emu-loop] load state failed: {ex.Message}");
        }
    });

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
                if (_publishDebugSnapshots)
                {
                    // Reuse buffers across frames: the "new" snapshot
                    // mutates the existing backing arrays in place and
                    // returns a new record pointing at the same memory.
                    // Without in-place reuse, 12 MB/sec of gen-0
                    // allocation pressure (mostly from VRAM's 192 KB
                    // RGBA buffer on CGB) causes periodic GC pauses
                    // long enough to drop audio samples.
                    Volatile.Write(ref _paletteSnapshot, PaletteSnapshot.From(sys, _paletteSnapshot));
                    Volatile.Write(ref _vramSnapshot, VramSnapshot.From(sys, _vramSnapshot));
                    Volatile.Write(ref _memorySnapshot, MemorySnapshot.From(sys, MemoryViewAddress, _memorySnapshot));
                }
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
                if (lastBufferedAfterPush > HighWater && !_fastForward)
                {
                    while (!_disposed && !_paused && !_fastForward)
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
