using System.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.App.Services;

/// <summary>
/// Thin facade over <see cref="EmulatorRunner"/> + <see cref="AudioPipe"/>.
/// Keeps the public surface existing callers rely on while moving the
/// hot-loop onto a background thread and publishing frames through a
/// <see cref="FramePublisher"/>.
/// </summary>
public sealed class EmulatorHost : IAsyncDisposable
{
    private readonly EmulatorRunner _runner;
    private readonly AudioPipe _audio;
    private readonly FramePublisher _frames;
    private KeyboardInputBridge? _keyboard;
    private bool _audioInitialized;

    private const int DebugFrameDivisor = 4;   // 60 / 4 ≈ 15 Hz
    private int _debugFrameCounter;
    private long _fpsFrameCount;
    private long _fpsLastStamp;

    public EmulatorHost(AudioPipe audio)
    {
        _audio = audio;
        _frames = new FramePublisher(160 * 144 * 4);
        _runner = new EmulatorRunner(audio);
        _runner.StateChanged += () => StateChanged?.Invoke();
        _runner.FatalError += ex => { LastError = ex; StateChanged?.Invoke(); };
        _runner.FrameCompleted += OnFrameCompleted;
    }

    public double Fps { get; private set; }
    public Exception? LastError { get; private set; }
    public GameBoySystem? System { get; private set; }
    public byte[]? OriginalRom { get; private set; }
    public bool IsPaused => _runner.IsPaused;
    public FramePublisher Frames => _frames;
    public AudioPipe Audio => _audio;

    public event Action? FrameReady;
    public event Action? StateChanged;
    /// <summary>Fires ~15 Hz while running — for live debug views.</summary>
    public event Action? DebugTick;

    public void RaiseStateChanged() => StateChanged?.Invoke();
    public void AttachKeyboard(KeyboardInputBridge keyboard) => _keyboard = keyboard;

    public Task LoadAsync(ReadOnlyMemory<byte> romBytes, HardwareMode mode)
    {
        _runner.Pause();
        var cart = CartridgeFactory.Load(romBytes.Span);
        System = new GameBoySystem(mode, cart);
        OriginalRom = romBytes.ToArray();
        _runner.SetSystem(System);
        StateChanged?.Invoke();
        return Task.CompletedTask;
    }

    public void AttachDebugSystem(GameBoySystem system)
    {
        _runner.Pause();
        System = system;
        _runner.SetSystem(System);
        StateChanged?.Invoke();
    }

    public async Task RunAsync()
    {
        if (System is null) return;

        if (!_audioInitialized)
        {
            await _audio.InitAsync();
            if (_keyboard is not null) await _keyboard.EnsureRegisteredAsync();
            _audioInitialized = true;
        }

        _fpsFrameCount = 0;
        _fpsLastStamp = Stopwatch.GetTimestamp();
        _debugFrameCounter = 0;
        _runner.Resume();

        // Keep the method awaitable for existing callers. Return when the
        // runner parks (pause / breakpoint). Poll cadence matches the old
        // per-frame cadence; this task is just a completion signal.
        while (!IsPaused)
        {
            await Task.Delay(50);
        }
        Fps = 0;
    }

    public void Pause() => _runner.Pause();

    public void StepInstruction()
    {
        if (System is null || !IsPaused) return;
        System.StepInstruction();
        StateChanged?.Invoke();
    }

    private void OnFrameCompleted()
    {
        var sys = System;
        if (sys is null) return;

        // Copy PPU's front framebuffer into the publisher's back slot.
        var back = _frames.AcquireBack();
        sys.Framebuffer.Front.CopyTo(back);
        _frames.PublishBack(back);

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
            StateChanged?.Invoke();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _runner.Dispose();
        await _audio.DisposeAsync();
    }
}
