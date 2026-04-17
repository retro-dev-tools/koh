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

    public void AttachKeyboard(KeyboardInputBridge keyboard) => _keyboard = keyboard;
    public GameBoySystem? System { get; private set; }
    public byte[]? OriginalRom { get; private set; }
    public event Action? FrameReady;
    public event Action? StateChanged;

    public void RaiseStateChanged() => StateChanged?.Invoke();

    public bool IsPaused { get; set; } = true;

    public EmulatorHost(FramePacer framePacer, WebAudioBridge webAudio)
    {
        _framePacer = framePacer;
        _webAudio = webAudio;
    }

    public void Load(ReadOnlyMemory<byte> romBytes, HardwareMode mode)
    {
        var cart = CartridgeFactory.Load(romBytes.Span);
        System = new GameBoySystem(mode, cart);
        OriginalRom = romBytes.ToArray();
        IsPaused = true;
        StateChanged?.Invoke();
    }

    public void AttachDebugSystem(GameBoySystem system)
    {
        System = system;
        IsPaused = true;
        StateChanged?.Invoke();
    }

    public async Task RunAsync()
    {
        if (System is null) return;
        IsPaused = false;

        if (!_audioInitialized)
        {
            await _webAudio.InitAsync();
            if (_keyboard is not null) await _keyboard.EnsureRegisteredAsync();
            _audioInitialized = true;
        }

        _fpsFrameCount = 0;
        _fpsLastStamp = Stopwatch.GetTimestamp();

        while (!IsPaused && System is not null)
        {
            var result = System.RunFrame();
            FrameReady?.Invoke();

            _fpsFrameCount++;
            long now = Stopwatch.GetTimestamp();
            long elapsed = now - _fpsLastStamp;
            if (elapsed >= Stopwatch.Frequency)   // 1 second
            {
                Fps = _fpsFrameCount * (double)Stopwatch.Frequency / elapsed;
                _fpsFrameCount = 0;
                _fpsLastStamp = now;

                // Only fire the full state-changed signal once per second so
                // the UI can pick up FPS / ROM-title changes. Per-frame
                // StateChanged forces every subscribed component (shell, CPU
                // dashboard, playback controls, save-state controls) to
                // re-render at 60 Hz — that's measurable load on the UI
                // thread and was eating into the frame budget the audio
                // pipeline needs to stay steady.
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
