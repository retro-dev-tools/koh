using Koh.Boot;
using Koh.Debugger.Session;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Boot;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Debugger;

public sealed class DebugSession
{
    public GameBoySystem? System { get; private set; }
    public DebugInfoLoader DebugInfo { get; } = new();
    public BreakpointManager Breakpoints { get; } = new();
    public WatchpointHook Watchpoints { get; }

    public DebugSession()
    {
        Watchpoints = new WatchpointHook(this);
    }

    public volatile bool PauseRequested;

    public event Action? Launched;

    public bool IsLaunched => System is not null;

    public void Launch(
        ReadOnlyMemory<byte> romBytes,
        ReadOnlyMemory<byte> kdbgBytes,
        HardwareMode? mode = null
    )
    {
        var cart = CartridgeFactory.Load(romBytes.Span);
        var system = new GameBoySystem(cart, mode);
        system.LoadBootRom(
            BootRom.FromBytes(system.Mode == HardwareMode.Cgb ? KohBootRoms.Cgb : KohBootRoms.Dmg)
        );
        // Run the boot ROM before hooks attach, so the session starts at the cartridge entry.
        // A boot ROM that locks up (bad header) stops in place after the cap.
        for (int i = 0; i < 10_000_000 && system.BootRomMapped; i++)
            system.StepInstruction();
        DebugInfo.Load(kdbgBytes);
        AdoptSystem(system);
    }

    /// <summary>
    /// Install an already-constructed <see cref="GameBoySystem"/> and
    /// wire the session's watchpoints + breakpoint checker onto it.
    /// Use this instead of <see cref="Launch"/> when the emulator
    /// host owns the system lifecycle and the debugger is attaching
    /// to an existing instance (the typical KohUI-emulator + DAP
    /// adapter flow) rather than booting the ROM itself.
    /// </summary>
    public void AdoptSystem(GameBoySystem system)
    {
        System = system;
        System.Mmu.Hook = Watchpoints;

        // Wire breakpoint halting: at each instruction boundary, consult
        // the BreakpointManager using the current PC. Below $4000 is
        // fixed bank 0; above that the MBC reports the active bank.
        System.BreakpointChecker = pc =>
        {
            byte bank = pc >= 0x4000 ? System.Cartridge.CurrentRomBank : (byte)0;
            var addr = new Koh.Linker.Core.BankedAddress(bank, pc);
            return Breakpoints.ShouldBreak(
                addr,
                cond => System is { } gb && ExpressionEvaluator.Evaluate(cond, gb)
            );
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
