namespace Koh.Emulator.Core.Cartridge;

public readonly record struct CartridgeHeader(
    string Title,
    MapperKind MapperKind,
    int RomBanks,
    int RamBanks,
    bool CgbFlag,
    bool CgbOnly)
{
    public static CartridgeHeader Parse(ReadOnlySpan<byte> rom)
    {
        if (rom.Length < 0x150)
            throw new ArgumentException("ROM smaller than header size", nameof(rom));

        // Title: $0134-$0143. CGB uses the last byte as CGB flag.
        byte cgbByte = rom[0x143];
        bool cgbFlag = (cgbByte & 0x80) != 0;
        bool cgbOnly = cgbByte == 0xC0;
        int titleLen = cgbFlag ? 15 : 16;
        int titleEnd = 0x134;
        while (titleEnd < 0x134 + titleLen && rom[titleEnd] != 0) titleEnd++;
        string title = System.Text.Encoding.ASCII.GetString(rom[0x134..titleEnd]);

        byte cartType = rom[0x147];
        MapperKind mapper = cartType switch
        {
            0x00 => MapperKind.RomOnly,
            0x01 or 0x02 or 0x03 => MapperKind.Mbc1,
            0x0F or 0x10 or 0x11 or 0x12 or 0x13 => MapperKind.Mbc3,
            0x19 or 0x1A or 0x1B or 0x1C or 0x1D or 0x1E => MapperKind.Mbc5,
            _ => throw new NotSupportedException($"Cartridge type ${cartType:X2} not supported"),
        };

        int romBanks = rom[0x148] switch
        {
            0x00 => 2,
            0x01 => 4,
            0x02 => 8,
            0x03 => 16,
            0x04 => 32,
            0x05 => 64,
            0x06 => 128,
            0x07 => 256,
            0x08 => 512,
            _ => 2,
        };

        int ramBanks = rom[0x149] switch
        {
            0x00 => 0,
            0x01 => 0,   // unused historically
            0x02 => 1,
            0x03 => 4,
            0x04 => 16,
            0x05 => 8,
            _ => 0,
        };

        return new CartridgeHeader(title, mapper, romBanks, ramBanks, cgbFlag, cgbOnly);
    }
}
