namespace Koh.Emulator.Core;

/// <summary>
/// Central clock state for the emulator. One "system tick" equals one PPU dot
/// (4.194304 MHz). In CGB double-speed mode, the CPU advances two T-cycles
/// per system tick; in single-speed, one T-cycle per system tick.
/// </summary>
public sealed class SystemClock
{
    public ulong SystemTicks { get; internal set; }
    public ulong FrameSystemTicks { get; internal set; }
    public bool DoubleSpeed { get; internal set; }

    public const int SystemTicksPerFrame = 70224; // 154 scanlines × 456 dots

    public void AdvanceOne()
    {
        SystemTicks++;
        FrameSystemTicks++;
    }

    public void ResetFrameCounter() => FrameSystemTicks = 0;
}
