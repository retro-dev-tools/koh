namespace Koh.Debugger.Tests;

/// <summary>ROM images that pass the boot ROM's logo and header-checksum checks.</summary>
internal static class TestRom
{
    private static readonly byte[] NintendoLogo =
    [
        0xCE,
        0xED,
        0x66,
        0x66,
        0xCC,
        0x0D,
        0x00,
        0x0B,
        0x03,
        0x73,
        0x00,
        0x83,
        0x00,
        0x0C,
        0x00,
        0x0D,
        0x00,
        0x08,
        0x11,
        0x1F,
        0x88,
        0x89,
        0x00,
        0x0E,
        0xDC,
        0xCC,
        0x6E,
        0xE6,
        0xDD,
        0xDD,
        0xD9,
        0x99,
        0xBB,
        0xBB,
        0x67,
        0x63,
        0x6E,
        0x0E,
        0xEC,
        0xCC,
        0xDD,
        0xDC,
        0x99,
        0x9F,
        0xBB,
        0xB9,
        0x33,
        0x3E,
    ];

    /// <summary>A 32KB RomOnly image with a valid header; leave $0104-$014D alone.</summary>
    public static byte[] Create() => WithValidHeader(new byte[0x8000]);

    /// <summary>Stamp the logo and header checksum into <paramref name="rom"/>.</summary>
    public static byte[] WithValidHeader(byte[] rom)
    {
        NintendoLogo.CopyTo(rom.AsSpan(0x104));
        byte checksum = 0;
        for (int i = 0x0134; i <= 0x014C; i++)
            checksum = (byte)(checksum - rom[i] - 1);
        rom[0x14D] = checksum;
        return rom;
    }
}
