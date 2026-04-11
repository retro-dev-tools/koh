using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// Phase 1 PPU: advances a dot counter and LY. No rendering, no mode transitions
/// on the STAT IRQ line, no pixel FIFO. Phase 2 replaces this with the full
/// algorithmic fetcher model per §7.7.
/// </summary>
public sealed class Ppu
{
    public Framebuffer Framebuffer { get; } = new();

    public byte LY { get; private set; }
    public int Dot { get; private set; }
    public PpuMode Mode { get; private set; } = PpuMode.OamScan;

    private const int DotsPerScanline = 456;
    private const int ScanlinesPerFrame = 154;

    public void TickDot(ref Interrupts interrupts)
    {
        Dot++;
        if (Dot >= DotsPerScanline)
        {
            Dot = 0;
            LY++;
            if (LY == 144)
            {
                Mode = PpuMode.VBlank;
                interrupts.Raise(Interrupts.VBlank);
                Framebuffer.Flip();
            }
            else if (LY >= ScanlinesPerFrame)
            {
                LY = 0;
                Mode = PpuMode.OamScan;
            }
            else if (LY < 144)
            {
                Mode = PpuMode.OamScan;
            }
        }
    }

    public void Reset()
    {
        LY = 0;
        Dot = 0;
        Mode = PpuMode.OamScan;
    }
}
