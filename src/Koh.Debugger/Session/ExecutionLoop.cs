using Koh.Emulator.Core;
using Koh.Emulator.Core.Ppu;

namespace Koh.Debugger.Session;

/// <summary>
/// Cooperative run loop that drives <see cref="GameBoySystem.RunFrame"/>
/// and yields to the JS event loop between frames so DAP pause requests
/// can be processed. See §8.6.
/// </summary>
public sealed class ExecutionLoop
{
    private readonly DebugSession _session;
    public event Action<Framebuffer>? FramebufferReady;
    public event Action<StepResult>? StoppedOnBreak;

    public ExecutionLoop(DebugSession session) { _session = session; }

    public async Task RunAsync()
    {
        if (_session.System is not { } gb) return;

        while (!_session.PauseRequested)
        {
            var result = gb.RunFrame();
            FramebufferReady?.Invoke(gb.Framebuffer);

            if (result.Reason == StopReason.Breakpoint || result.Reason == StopReason.Watchpoint)
            {
                StoppedOnBreak?.Invoke(result);
                break;
            }

            await Task.Yield();
        }
    }
}
