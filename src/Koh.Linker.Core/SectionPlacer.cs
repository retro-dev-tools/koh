using Koh.Core.Binding;
using Koh.Core.Diagnostics;

namespace Koh.Linker.Core;

/// <summary>
/// Places sections into Game Boy memory regions, respecting fixed addresses,
/// bank constraints, and alignment.
/// </summary>
public sealed class SectionPlacer
{
    private readonly DiagnosticBag _diagnostics;

    // Memory region definitions: start address, end address (exclusive), bank count
    private static readonly MemoryRegion[] Regions =
    [
        new(SectionType.Rom0, 0x0000, 0x4000, 1),
        new(SectionType.RomX, 0x4000, 0x8000, 512), // up to 512 banks
        new(SectionType.Vram, 0x8000, 0xA000, 2),
        new(SectionType.Sram, 0xA000, 0xC000, 16),
        new(SectionType.Wram0, 0xC000, 0xD000, 1),
        new(SectionType.WramX, 0xD000, 0xE000, 8),
        new(SectionType.Oam, 0xFE00, 0xFEA0, 1),
        new(SectionType.Hram, 0xFF80, 0xFFFF, 1),
    ];

    public SectionPlacer(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Place all sections into memory. Sets PlacedAddress and PlacedBank on each section.
    /// </summary>
    public void PlaceAll(IReadOnlyList<LinkerSection> sections)
    {
        // Group by memory type
        var byType = sections.GroupBy(s => s.Type).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var region in Regions)
        {
            if (!byType.TryGetValue(region.Type, out var regionSections))
                continue;

            PlaceRegion(region, regionSections);
        }
    }

    private void PlaceRegion(MemoryRegion region, List<LinkerSection> sections)
    {
        // Sort: fixed address first, then by size descending (largest first = better packing).
        // List.Sort is not stable — two sections of equal size can swap between runs.
        // This is normally acceptable, but if reproducible output is needed in future,
        // add a tertiary key such as section name to make the order deterministic.
        sections.Sort((a, b) =>
        {
            if (a.FixedAddress.HasValue != b.FixedAddress.HasValue)
                return a.FixedAddress.HasValue ? -1 : 1;
            return b.Data.Length.CompareTo(a.Data.Length);
        });

        // Track free space per bank: bank → next free offset.
        // ROMX banks are numbered 1–511; bank 0 is reserved for ROM0 (the fixed window
        // at 0x0000–0x3FFF that is always mapped). Floating allocation therefore starts
        // at bank 1 for ROMX. For all other regions (VRAM, WRAM, etc.) banks start at 0.
        var bankUsage = new Dictionary<int, int>();
        int bankCount = region.Type == SectionType.Rom0 ? 1 :
                        region.Type == SectionType.RomX ? 512 : region.BankCount;
        int firstBank = region.Type == SectionType.RomX ? 1 : 0;

        for (int b = firstBank; b < bankCount; b++)
            bankUsage[b] = region.StartAddress;

        foreach (var section in sections)
        {
            if (section.FixedAddress.HasValue)
            {
                // Fixed address placement
                int bank = section.Bank ?? 0;
                section.PlacedAddress = section.FixedAddress.Value;
                section.PlacedBank = bank;

                // Update bank usage
                int endAddr = section.FixedAddress.Value + section.Data.Length;
                if (!bankUsage.ContainsKey(bank))
                    bankUsage[bank] = region.StartAddress;
                if (endAddr > bankUsage[bank])
                    bankUsage[bank] = endAddr;

                // Detect overlaps with previously placed sections in the same bank
                int secStart = section.FixedAddress.Value;
                int secEnd = secStart + section.Data.Length;
                foreach (var other in sections)
                {
                    if (ReferenceEquals(other, section) || other.PlacedAddress < 0)
                        continue;
                    if (other.PlacedBank != bank)
                        continue;
                    int otherStart = other.PlacedAddress;
                    int otherEnd = otherStart + other.Data.Length;
                    if (secStart < otherEnd && otherStart < secEnd)
                    {
                        int overlapStart = Math.Max(secStart, otherStart);
                        int overlapEnd = Math.Min(secEnd, otherEnd);
                        _diagnostics.Report(default,
                            $"Section '{section.Name}' overlaps with '{other.Name}' " +
                            $"at ${overlapStart:X4}-${overlapEnd:X4} in bank {bank}");
                    }
                }
            }
            else
            {
                // Find a bank with enough space
                int targetBank = section.Bank ?? -1;
                bool placed = false;

                if (targetBank >= 0)
                {
                    // Fixed bank, floating address
                    placed = TryPlaceInBank(section, region, bankUsage, targetBank);
                }
                else
                {
                    // Float both bank and address — find first bank with space.
                    // Start from firstBank: ROMX begins at bank 1 (bank 0 is ROM0's fixed window).
                    for (int b = firstBank; b < bankCount && !placed; b++)
                        placed = TryPlaceInBank(section, region, bankUsage, b);
                }

                if (!placed)
                {
                    int capacity = region.EndAddress - region.StartAddress;
                    if (section.Data.Length > capacity)
                    {
                        // Section exceeds single bank capacity
                        _diagnostics.Report(default,
                            $"Section '{section.Name}' ({section.Data.Length} bytes) " +
                            $"exceeds {section.Type} bank capacity ({capacity} bytes)");
                    }
                    else if (targetBank >= 0)
                    {
                        // Fixed bank but doesn't fit
                        int used = bankUsage.GetValueOrDefault(targetBank, region.StartAddress) - region.StartAddress;
                        int free = capacity - used;
                        _diagnostics.Report(default,
                            $"Section '{section.Name}' ({section.Data.Length} bytes) " +
                            $"does not fit in bank {targetBank} of {section.Type} " +
                            $"({free} bytes free space)");
                    }
                    else
                    {
                        // Floating but no bank has room — find largest free space
                        int largestFree = 0;
                        for (int b = firstBank; b < bankCount; b++)
                        {
                            int used = bankUsage.GetValueOrDefault(b, region.StartAddress) - region.StartAddress;
                            int free = capacity - used;
                            if (free > largestFree)
                                largestFree = free;
                        }
                        _diagnostics.Report(default,
                            $"Section '{section.Name}' ({section.Data.Length} bytes) " +
                            $"does not fit in any {section.Type} bank " +
                            $"(largest free space is {largestFree} bytes)");
                    }
                }
            }
        }
    }

    private static bool TryPlaceInBank(LinkerSection section, MemoryRegion region,
        Dictionary<int, int> bankUsage, int bank)
    {
        if (!bankUsage.ContainsKey(bank))
            bankUsage[bank] = region.StartAddress;

        int addr = bankUsage[bank];
        int endAddr = addr + section.Data.Length;

        if (endAddr > region.EndAddress)
            return false; // doesn't fit

        section.PlacedAddress = addr;
        section.PlacedBank = bank;
        bankUsage[bank] = endAddr;
        return true;
    }

    private readonly record struct MemoryRegion(
        SectionType Type, int StartAddress, int EndAddress, int BankCount);
}
