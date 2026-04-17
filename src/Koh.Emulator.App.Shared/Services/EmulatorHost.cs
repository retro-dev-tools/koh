using System.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.App.Services;

public sealed class EmulatorHost
{
    private readonly FramePacer _framePacer;
    private readonly WebAudioBridge _webAudio;
    private KeyboardInputBridge? _keyboard;
    private bool _audioInitialized;

    // Rolling FPS sampled once per second from the RunAsync loop. 0 when
    // paused or no ROM is loaded. The Game Boy nominal refresh is ~59.73 Hz.
    public double Fps { get; private set; }
    private int _fpsFrameCount;
    private long _fpsLastStamp;

    // Task-level reentrancy guard. Calling RunAsync while a loop is already
    // running returns the existing task; re-loading a ROM pauses + awaits the
    // active loop before installing the new System so we never run two loops
    // against one GameBoySystem (which doubled the emu speed, doubled audio
    // drain, and raced on FPS bookkeeping before this fix).
    private Task? _runningTask;

    // Throttled frame signal for live debug views (CpuDashboard etc.) — fires
    // ~15 Hz while running, vs StateChanged which fires 1 Hz for chrome that
    // only cares about FPS / ROM title. Without this, the Diagnostics drawer
    // strobes once per second after the 1 Hz StateChanged change.
    private const int DebugFrameDivisor = 4;   // 60 / 4 ≈ 15 Hz
    private int _debugFrameCounter;

    public void AttachKeyboard(KeyboardInputBridge keyboard) => _keyboard = keyboard;
    public GameBoySystem? System { get; private set; }
    public byte[]? OriginalRom { get; private set; }
    public event Action? FrameReady;
    public event Action? StateChanged;
    /// <summary>
    /// Fires ~15 Hz while running. Debug views (register dashboards, memory
    /// viewers) should subscribe here instead of StateChanged so they stay
    /// live without forcing the full chrome to re-render per frame.
    /// </summary>
    public event Action? DebugTick;

    public void RaiseStateChanged() => StateChanged?.Invoke();

    public bool IsPaused { get; set; } = true;

    public EmulatorHost(FramePacer framePacer, WebAudioBridge webAudio)
    {
        _framePacer = framePacer;
        _webAudio = webAudio;
    }

    public async Task LoadAsync(ReadOnlyMemory<byte> romBytes, HardwareMode mode)
    {
        // Park any in-flight run loop and wait for it to actually exit before
        // swapping System — otherwise the suspended loop wakes on rAF and
        // starts driving the new System alongside the fresh RunAsync we're
        // about to fire.
        Pause();
        if (_runningTask is { IsCompleted: false } pending)
        {
            try { await pending; } catch { /* loop exceptions surface on next RunAsync */ }
        }
        _runningTask = null;

        var cart = CartridgeFactory.Load(romBytes.Span);
        System = new GameBoySystem(mode, cart);
        OriginalRom = romBytes.ToArray();
        IsPaused = true;
        StateChanged?.Invoke();
    }

    public void AttachDebugSystem(GameBoySystem system)
    {
        Pause();
        System = system;
        IsPaused = true;
        StateChanged?.Invoke();
    }

    public Task RunAsync()
    {
        if (System is null) return Task.CompletedTask;

        // Reentrancy guard: if a loop is already running, return its Task so
        // the caller can still await "completion" semantically. Avoids the
        // previous double-RunAsync race where both loops drove the same
        // GameBoySystem at 2× real-time.
        if (_runningTask is { IsCompleted: false } inflight)
            return inflight;

        _runningTask = RunLoopAsync();
        return _runningTask;
    }

    private async Task RunLoopAsync()
    {
        IsPaused = false;

        if (!_audioInitialized)
        {
            await _webAudio.InitAsync();
            if (_keyboard is not null) await _keyboard.EnsureRegisteredAsync();
            _audioInitialized = true;
        }

        _fpsFrameCount = 0;
        _fpsLastStamp = Stopwatch.GetTimestamp();
        _debugFrameCounter = 0;

        while (!IsPaused && System is not null)
        {
            var result = System.RunFrame();
            FrameReady?.Invoke();

            if ((++_debugFrameCounter % DebugFrameDivisor) == 0)
                DebugTick?.Invoke();

            _fpsFrameCount++;
            long now = Stopwatch.GetTimestamp();
            long elapsed = now - _fpsLastStamp;
            if (elapsed >= Stopwatch.Frequency)   // 1 second
            {
                Fps = _fpsFrameCount * (double)Stopwatch.Frequency / elapsed;
                _fpsFrameCount = 0;
                _fpsLastStamp = now;

                // Chrome-level state-changed (ROM title, FPS text, playback
                // button states). Stays at 1 Hz so a full re-render of every
                // subscribed component doesn't fire 60×/s. Live debug views
                // use DebugTick instead.
                StateChanged?.Invoke();
            }

            await DrainAudioAsync();

            if (result.Reason == StopReason.Breakpoint || result.Reason == StopReason.Watchpoint)
            {
                IsPaused = true;
                break;
            }

            await _framePacer.WaitForNextFrameAsync();
        }

        Fps = 0;
    }

    private async ValueTask DrainAudioAsync()
    {
        if (System is null) return;
        int available = System.Apu.SampleBuffer.Available;
        if (available == 0) return;
        var buf = new short[available];
        int n = System.Apu.SampleBuffer.Drain(buf);
        if (n > 0) await _webAudio.PushAsync(buf.AsMemory(0, n));
    }

    public void StepInstruction()
    {
        if (System is null) return;
        System.StepInstruction();
        StateChanged?.Invoke();
    }

    public void Pause()
    {
        IsPaused = true;
        System?.RunGuard.RequestStop();
    }
}
