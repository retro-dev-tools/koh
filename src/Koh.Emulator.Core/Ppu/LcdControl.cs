namespace Koh.Emulator.Core.Ppu;

/// <summary>
/// LCDC register ($FF40) decode helpers. Stored as a plain byte; these helpers
/// read bit fields without mutating state.
/// </summary>
public static class LcdControl
{
    public const byte LcdEnable           = 1 << 7;
    public const byte WindowTileMapArea   = 1 << 6;
    public const byte WindowEnable        = 1 << 5;
    public const byte BgWindowTileDataArea = 1 << 4;
    public const byte BgTileMapArea       = 1 << 3;
    public const byte ObjSize8x16         = 1 << 2;
    public const byte ObjEnable           = 1 << 1;
    public const byte BgWindowEnableOrPriority = 1 << 0;

    public static bool IsSet(byte lcdc, byte flag) => (lcdc & flag) != 0;
    public static ushort BgTileMapBase(byte lcdc) => IsSet(lcdc, BgTileMapArea) ? (ushort)0x9C00 : (ushort)0x9800;
    public static ushort WindowTileMapBase(byte lcdc) => IsSet(lcdc, WindowTileMapArea) ? (ushort)0x9C00 : (ushort)0x9800;
    public static bool BgWindowUnsignedTileData(byte lcdc) => IsSet(lcdc, BgWindowTileDataArea);
    public static int SpriteHeight(byte lcdc) => IsSet(lcdc, ObjSize8x16) ? 16 : 8;
}
