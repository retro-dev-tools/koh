using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.App.Services;

public sealed class EmulatorHost
{
    private readonly FramePacer _framePacer;
    public GameBoySystem? System { get; private set; }
    public event Action? FrameReady;
    public event Action? StateChanged;

    public bool IsPaused { get; set; } = true;

    public EmulatorHost(FramePacer framePacer) { _framePacer = framePacer; }

    public void Load(ReadOnlyMemory<byte> romBytes, HardwareMode mode)
    {
        var cart = CartridgeFactory.Load(romBytes.Span);
        System = new GameBoySystem(mode, cart);
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

        while (!IsPaused && System is not null)
        {
            var result = System.RunFrame();
            FrameReady?.Invoke();
            StateChanged?.Invoke();

            if (result.Reason == StopReason.Breakpoint || result.Reason == StopReason.Watchpoint)
            {
                IsPaused = true;
                break;
            }

            await _framePacer.WaitForNextFrameAsync();
        }
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
