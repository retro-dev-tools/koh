namespace Koh.Emulator.Core.Ppu;

public enum PpuMode : byte
{
    HBlank = 0,
    VBlank = 1,
    OamScan = 2,
    Drawing = 3,
}
