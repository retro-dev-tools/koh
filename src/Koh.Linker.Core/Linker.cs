using Koh.Core.Binding;
using Koh.Core.Diagnostics;

namespace Koh.Linker.Core;

/// <summary>
/// Result of the link operation.
/// </summary>
public sealed class LinkResult
{
    public byte[]? RomData { get; }
    public IReadOnlyList<LinkerSymbol> Symbols { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    /// <summary>
    /// All line-map runs from every input section, resolved to absolute
    /// (bank, windowed GB address). Empty when no inputs carry line
    /// info (v1 kobj, or the assembler didn't set source locations).
    /// DebugInfoPopulator turns these into .kdbg AddressMap entries.
    /// </summary>
    public IReadOnlyList<ResolvedLineMapEntry> LineMap { get; }
    public bool Success { get; }

    internal LinkResult(byte[]? romData, IReadOnlyList<LinkerSymbol> symbols,
        IReadOnlyList<Diagnostic> diagnostics,
        IReadOnlyList<ResolvedLineMapEntry> lineMap)
    {
        RomData = romData;
        Symbols = symbols;
        Diagnostics = diagnostics;
        LineMap = lineMap;

        Success = true;
        for (int i = 0; i < diagnostics.Count; i++)
            if (diagnostics[i].Severity == DiagnosticSeverity.Error)
            {
                Success = false;
                break;
            }
    }
}

/// <summary>
/// Links multiple .kobj object files into a single Game Boy ROM.
/// </summary>
public sealed class Linker
{
    private readonly DiagnosticBag _diagnostics = new();

    public LinkResult Link(IReadOnlyList<LinkerInput> inputs)
    {
        // 1. Collect all sections and symbols
        var resolver = new SymbolResolver(_diagnostics);
        var sections = new List<LinkerSection>();

        foreach (var input in inputs)
        {
            resolver.AddSymbols(input);
            foreach (var section in input.Model.Sections)
                sections.Add(new LinkerSection(section, input.FilePath));
        }

        // 2. Place sections into memory
        var placer = new SectionPlacer(_diagnostics);
        placer.PlaceAll(sections);

        // 3. Resolve symbol addresses
        resolver.ResolveAddresses(sections);

        // 4. Apply patches (resolve linker-time expressions)
        ApplyPatches(sections, resolver);

        // 5. Build ROM
        byte[]? rom = null;
        if (!HasErrors())
            rom = RomWriter.BuildRom(sections);

        // 6. Resolve each input section's per-byte line map into the
        //    bank + 16-bit windowed-address form the .kdbg expects. Done
        //    after placement so every section has a PlacedAddress.
        var resolvedLineMap = ResolveLineMap(sections);

        return new LinkResult(rom, resolver.AllSymbols, _diagnostics.ToList(), resolvedLineMap);
    }

    private static IReadOnlyList<ResolvedLineMapEntry> ResolveLineMap(List<LinkerSection> sections)
    {
        var result = new List<ResolvedLineMapEntry>();
        foreach (var section in sections)
        {
            if (section.PlacedAddress < 0 || section.LineMap.Count == 0) continue;
            byte bank = (byte)section.PlacedBank;
            foreach (var entry in section.LineMap)
            {
                // PlacedAddress is the windowed GB address of offset 0 in the
                // section (0x0000–0x3FFF for ROM0, 0x4000–0x7FFF for ROMX, etc.).
                // Offsets add directly; we keep only the low 16 bits because
                // that's what NamedPipe DAP + .kdbg use to drive breakpoints.
                ushort address = (ushort)((section.PlacedAddress + entry.Offset) & 0xFFFF);
                result.Add(new ResolvedLineMapEntry(bank, address, entry.ByteCount, entry.File, entry.Line));
            }
        }
        return result;
    }

    private void ApplyPatches(List<LinkerSection> sections, SymbolResolver resolver)
    {
        foreach (var section in sections)
        {
            foreach (var patch in section.Patches)
            {
                // Expression trees are not serialised in .kobj yet.
                // Single-file assembly is unaffected: PatchResolver resolves all
                // intra-file forward references before the .kobj is written, so
                // every patch that reaches this linker has Expression == null.
                //
                // Cross-file references via .kobj require expression serialization
                // in KobjWriter/KobjReader. For RGBDS output, use --format rgbds
                // which handles cross-file refs via RPN patches in the .o format.
                // A non-null expression here means the patch was not resolved by
                // PatchResolver — report it as an error.
                if (patch.Expression == null) continue;

                _diagnostics.Report(patch.DiagnosticSpan,
                    $"Unresolved cross-file patch at offset {patch.Offset} in section " +
                    $"'{patch.SectionName}': expression trees are not serialised in " +
                    ".kobj yet — cross-file references are not supported in this version.");
            }
        }
    }

    private bool HasErrors()
    {
        // Perf: iterate the bag directly — ToList() returns the internal list (no copy),
        // but the name implies a copy and the contract does not forbid one in future.
        foreach (var d in _diagnostics)
            if (d.Severity == DiagnosticSeverity.Error)
                return true;
        return false;
    }
}
