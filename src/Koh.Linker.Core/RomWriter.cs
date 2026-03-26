using Koh.Core.Binding;

namespace Koh.Linker.Core;

/// <summary>
/// Writes placed sections into a flat Game Boy ROM image and fixes up
/// the header checksum bytes.
/// </summary>
public static class RomWriter
{
    /// <summary>
    /// Build a ROM image from placed sections. The ROM is padded to the
    /// nearest power-of-two size (minimum 32KB).
    /// </summary>
    public static byte[] BuildRom(IReadOnlyList<LinkerSection> sections, int minSize = 0x8000)
    {
        // Determine ROM size from placed sections.
        // ROMX sections use a windowed GB address (0x4000–0x7FFF); the physical flat-ROM
        // offset is bank * 0x4000 + (addr - 0x4000). ROM0 addresses are already physical.
        int maxAddr = minSize;
        foreach (var s in sections)
        {
            if (s.Type is SectionType.Rom0 or SectionType.RomX)
            {
                int physEnd = PhysicalOffset(s) + s.Data.Length;
                if (physEnd > maxAddr) maxAddr = physEnd;
            }
        }

        // Round up to power of two
        int romSize = NextPowerOfTwo(maxAddr);
        var rom = new byte[romSize];

        // Copy section data into ROM at physical offsets.
        // FixHeaderChecksum must run before FixGlobalChecksum because the global
        // checksum covers the already-corrected header checksum byte at $014D.
        foreach (var s in sections)
        {
            if (s.Type is SectionType.Rom0 or SectionType.RomX)
            {
                Array.Copy(s.Data, 0, rom, PhysicalOffset(s), s.Data.Length);
            }
        }

        // Header checksum must be fixed before global, because global covers byte $014D.
        FixHeaderChecksum(rom);
        FixGlobalChecksum(rom);

        return rom;
    }

    /// <summary>
    /// Header checksum at $014D: complement of sum of bytes $0134-$014C.
    /// </summary>
    private static void FixHeaderChecksum(byte[] rom)
    {
        if (rom.Length < 0x0150) return;

        byte checksum = 0;
        for (int i = 0x0134; i <= 0x014C; i++)
            checksum = (byte)(checksum - rom[i] - 1);
        rom[0x014D] = checksum;
    }

    /// <summary>
    /// Global checksum at $014E-$014F: sum of all ROM bytes except $014E-$014F.
    /// </summary>
    private static void FixGlobalChecksum(byte[] rom)
    {
        if (rom.Length < 0x0150) return;

        ushort checksum = 0;
        for (int i = 0; i < rom.Length; i++)
        {
            if (i == 0x014E || i == 0x014F) continue;
            checksum += rom[i];
        }
        rom[0x014E] = (byte)(checksum >> 8);
        rom[0x014F] = (byte)(checksum & 0xFF);
    }

    /// <summary>
    /// Returns the physical flat-ROM byte offset for a placed section.
    /// ROM0 sections are already at their physical address (bank 0 = 0x0000–0x3FFF).
    /// ROMX sections use a windowed address (0x4000–0x7FFF); the physical offset is
    /// bank * 0x4000 + (addr - 0x4000), mapping bank 1 to 0x4000, bank 2 to 0x8000, etc.
    /// </summary>
    private static int PhysicalOffset(LinkerSection s) =>
        s.Type == SectionType.RomX
            ? s.PlacedBank * 0x4000 + (s.PlacedAddress - 0x4000)
            : s.PlacedAddress;

    // Perf: bit-scatter + OR fills all lower bits, final +1 carries to next power.
    // Correct for value in [1, 0x40000000]. GB ROMs max at 8 MB (0x800000), well within range.
    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1) return 1;
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1;
    }
}
