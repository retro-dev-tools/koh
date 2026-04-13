namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// Parsed OAM entry. OAM contains 40 entries of 4 bytes each: Y, X, tile, attributes.
/// </summary>
public readonly struct ObjectAttributes
{
    public readonly byte Y;
    public readonly byte X;
    public readonly byte Tile;
    public readonly byte Flags;

    public ObjectAttributes(byte y, byte x, byte tile, byte flags)
    {
        Y = y; X = x; Tile = tile; Flags = flags;
    }

    public const byte FlagBgPriority = 1 << 7;
    public const byte FlagYFlip      = 1 << 6;
    public const byte FlagXFlip      = 1 << 5;
    public const byte FlagDmgPalette = 1 << 4;   // OBP0 vs OBP1 on DMG
    public const byte FlagCgbVramBank = 1 << 3;  // VRAM bank for CGB
    public const byte CgbPaletteMask  = 0x07;    // bits 0..2 CGB palette index

    public bool BgPriority => (Flags & FlagBgPriority) != 0;
    public bool YFlip => (Flags & FlagYFlip) != 0;
    public bool XFlip => (Flags & FlagXFlip) != 0;
    public bool UsesObp1 => (Flags & FlagDmgPalette) != 0;
    public bool CgbVramBank1 => (Flags & FlagCgbVramBank) != 0;
    public byte CgbPalette => (byte)(Flags & CgbPaletteMask);

    public static ObjectAttributes Parse(ReadOnlySpan<byte> oamBytes, int index)
    {
        int baseIdx = index * 4;
        return new ObjectAttributes(
            oamBytes[baseIdx + 0],
            oamBytes[baseIdx + 1],
            oamBytes[baseIdx + 2],
            oamBytes[baseIdx + 3]);
    }
}
