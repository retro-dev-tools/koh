namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// STAT register ($FF41) with internal IRQ-line tracking. The IRQ line is the
/// OR of all enabled sources; an edge (low→high) raises IF.STAT.
/// </summary>
public struct StatRegister
{
    public const byte LyLycIrqEnable   = 1 << 6;
    public const byte OamIrqEnable     = 1 << 5;
    public const byte VBlankIrqEnable  = 1 << 4;
    public const byte HBlankIrqEnable  = 1 << 3;
    public const byte LyLycFlag        = 1 << 2;

    public byte UserBits;   // bits 3..6 user-writable, bits 0..2 are computed

    public byte Read(PpuMode mode, bool lyEqualsLyc)
    {
        byte modeBits = (byte)((int)mode & 0x03);
        byte coincidence = lyEqualsLyc ? LyLycFlag : (byte)0;
        byte userMask = 0b_0111_1000;
        return (byte)((UserBits & userMask) | modeBits | coincidence | 0x80);
    }

    public void Write(byte value) => UserBits = (byte)(value & 0b_0111_1000);
}
