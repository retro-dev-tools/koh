using Koh.Debugger.Session;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Debugger;

public sealed class DebugSession
{
    public GameBoySystem? System { get; private set; }
    public DebugInfoLoader DebugInfo { get; } = new();
    public BreakpointManager Breakpoints { get; } = new();
    public WatchpointHook Watchpoints { get; }

    public DebugSession() { Watchpoints = new WatchpointHook(this); }

    public volatile bool PauseRequested;

    public event Action? Launched;

    public bool IsLaunched => System is not null;

    public void Launch(ReadOnlyMemory<byte> romBytes, ReadOnlyMemory<byte> kdbgBytes, HardwareMode mode)
    {
        var cart = CartridgeFactory.Load(romBytes.Span);
        System = new GameBoySystem(mode, cart);
        System.Mmu.Hook = Watchpoints;
        DebugInfo.Load(kdbgBytes);

        // Wire breakpoint halting: at each instruction boundary, consult
        // the BreakpointManager using the current PC. We only have a bank
        // byte for banked addresses >= 0x4000; below that, bank 0 is fixed.
        System.BreakpointChecker = pc =>
        {
            byte bank = pc >= 0x4000 ? System.Cartridge.CurrentRomBank : (byte)0;
            var addr = new Koh.Linker.Core.BankedAddress(bank, pc);
            return Breakpoints.ShouldBreak(addr, cond =>
                System is { } gb && ExpressionEvaluator.Evaluate(cond, gb));
        };

        Launched?.Invoke();
    }

    public void Terminate()
    {
        System?.RunGuard.RequestStop();
        PauseRequested = true;
        System = null;
    }
}
