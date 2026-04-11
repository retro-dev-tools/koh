namespace Koh.Emulator.Core.Cartridge;

public enum MapperKind : byte
{
    RomOnly = 0,
    Mbc1 = 1,
    // Mbc3 and Mbc5 are added in Phase 4. Intermediate values reserved.
}
