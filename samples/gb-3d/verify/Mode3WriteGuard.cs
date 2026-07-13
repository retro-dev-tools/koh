using Koh.Emulator.Core;
using Koh.Emulator.Core.Debug;
using Koh.Emulator.Core.Ppu;

namespace Koh.Verify;

/// <summary>
/// Attaches to a running <see cref="GameBoySystem"/> and records every CPU write that lands in VRAM
/// ($8000-$9FFF) while the PPU owns the bus (mode 3, "Drawing", with the LCD on). Mmu.WriteByte fires
/// the hook *before* applying its own mode-3 lockout, so this sees the write attempt itself — the same
/// thing real hardware would silently drop — independent of whether the emulator's Mmu happens to model
/// the drop. A ROM that's genuinely timing-safe (edge-synchronized bursts, or writing only with the LCD
/// off / during vblank) never attempts a VRAM write while Mode == Drawing, so <see cref="Violations"/>
/// stays empty across the whole run.
/// </summary>
public sealed class Mode3WriteGuard : MemoryHook
{
    private readonly GameBoySystem _system;

    public List<(ushort Address, byte Value, byte Ly)> Violations { get; } = new();

    public Mode3WriteGuard(GameBoySystem system) => _system = system;

    public override void OnRead(ushort address, byte value) { }

    public override void OnWrite(ushort address, byte value)
    {
        if (address < 0x8000 || address >= 0xA000)
            return;
        if ((_system.Ppu.LCDC & 0x80) == 0)
            return; // LCD off: PPU doesn't own the bus, no lockout in effect
        if (_system.Ppu.Mode != PpuMode.Drawing)
            return;
        Violations.Add((address, value, _system.Ppu.LY));
    }
}
