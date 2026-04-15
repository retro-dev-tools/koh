using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.App.Services;

public sealed class EmulatorHost
{
    private readonly FramePacer _framePacer;
    private readonly WebAudioBridge _webAudio;
    private bool _audioInitialized;
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
            _audioInitialized = true;
        }

        while (!IsPaused && System is not null)
        {
            var result = System.RunFrame();
            FrameReady?.Invoke();
            StateChanged?.Invoke();

            await DrainAudioAsync();

            if (result.Reason == StopReason.Breakpoint || result.Reason == StopReason.Watchpoint)
            {
                IsPaused = true;
                break;
            }

            await _framePacer.WaitForNextFrameAsync();
        }
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
