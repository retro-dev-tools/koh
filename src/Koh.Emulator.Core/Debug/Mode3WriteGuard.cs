using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.Core.Debug;

/// <summary>
/// Attaches to a running <see cref="GameBoySystem"/> and records every CPU write that lands in VRAM
/// ($8000-$9FFF) while the PPU owns the bus (mode 3, "Drawing", with the LCD on). <c>Mmu.WriteByte</c>
/// fires the hook *before* applying its own mode-3 lockout, so this sees the write attempt itself — the
/// same thing real hardware would silently drop — independent of whether the emulator's Mmu happens to
/// model the drop. A ROM that's genuinely timing-safe (edge-synchronized bursts, or writing only with
/// the LCD off / during vblank) never attempts a VRAM write while Mode == Drawing, so
/// <see cref="Violations"/> stays empty across the whole run.
/// </summary>
public sealed class Mode3WriteGuard(GameBoySystem system) : MemoryHook
{
    private readonly List<Mode3Violation> _violations = new();

    public IReadOnlyList<Mode3Violation> Violations => _violations;

    public override void OnRead(ushort address, byte value) { }

    public override void OnWrite(ushort address, byte value)
    {
        if (address < 0x8000 || address >= 0xA000)
            return;
        if ((system.Ppu.LCDC & 0x80) == 0)
            return; // LCD off: PPU doesn't own the bus, no lockout in effect
        if (system.Ppu.Mode != PpuMode.Drawing)
            return;
        _violations.Add(new Mode3Violation(address, value, system.Ppu.LY, system.Cpu.Registers.Pc));
    }
}

/// <summary>
/// One CPU write that landed in VRAM during PPU mode 3 (Drawing) with the LCD on. <see cref="Pc"/> is
/// the raw program counter at the time of the write — resolving it to a symbol/file:line is the
/// caller's job (e.g. via <c>Koh.Debugger.Session.SourceMap</c>/<c>SymbolMap</c> against a loaded
/// <c>.kdbg</c>), since this project stays free of any <c>Koh.Debugger</c>/linker dependency.
/// </summary>
public readonly record struct Mode3Violation(ushort Address, byte Value, byte Ly, ushort Pc);
