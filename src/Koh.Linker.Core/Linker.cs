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

    internal LinkResult(
        byte[]? romData,
        IReadOnlyList<LinkerSymbol> symbols,
        IReadOnlyList<Diagnostic> diagnostics,
        IReadOnlyList<ResolvedLineMapEntry> lineMap
    )
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
            if (section.PlacedAddress < 0 || section.LineMap.Count == 0)
                continue;
            byte bank = (byte)section.PlacedBank;
            foreach (var entry in section.LineMap)
            {
                // PlacedAddress is the windowed GB address of offset 0 in the
                // section (0x0000–0x3FFF for ROM0, 0x4000–0x7FFF for ROMX, etc.).
                // Offsets add directly; we keep only the low 16 bits because
                // that's what NamedPipe DAP + .kdbg use to drive breakpoints.
                ushort address = (ushort)((section.PlacedAddress + entry.Offset) & 0xFFFF);
                result.Add(
                    new ResolvedLineMapEntry(bank, address, entry.ByteCount, entry.File, entry.Line)
                );
            }
        }
        return result;
    }

    private void ApplyPatches(List<LinkerSection> sections, SymbolResolver resolver)
    {
        foreach (var section in sections)
        {
            if (section.PlacedAddress < 0)
                continue; // not placed; skip

            foreach (var patch in section.Patches)
            {
                // Patches with Expression!=null but no SymbolName are unresolvable
                // (RPN ASTs are not serialised in .kobj). Report and skip.
                if (patch.SymbolName == null)
                {
                    if (patch.Expression != null)
                    {
                        _diagnostics.Report(
                            patch.DiagnosticSpan,
                            $"Unresolved cross-file/cross-section patch at offset "
                                + $"${patch.Offset:X4} in section '{section.Name}': "
                                + "complex expressions are not yet supported in cross-section refs."
                        );
                    }
                    continue;
                }

                var sym = resolver.Lookup(patch.SymbolName, section.SourceFile);
                if (sym == null)
                {
                    _diagnostics.Report(
                        patch.DiagnosticSpan,
                        $"Undefined symbol '{patch.SymbolName}' "
                            + $"referenced from section '{section.Name}'"
                    );
                    continue;
                }

                long absValue = sym.AbsoluteAddress + patch.SymbolOffset;
                if (patch.SymbolShift != 0)
                    absValue >>= patch.SymbolShift;

                switch (patch.Kind)
                {
                    case PatchKind.Absolute8:
                        section.Data[patch.Offset] = (byte)(absValue & 0xFF);
                        break;
                    case PatchKind.Absolute16:
                        // Use only the windowed 16-bit address (handles banked ROMs correctly)
                        long addr16 = absValue & 0xFFFF;
                        section.Data[patch.Offset] = (byte)(addr16 & 0xFF);
                        section.Data[patch.Offset + 1] = (byte)((addr16 >> 8) & 0xFF);
                        break;
                    case PatchKind.Absolute32:
                        section.Data[patch.Offset] = (byte)(absValue & 0xFF);
                        section.Data[patch.Offset + 1] = (byte)((absValue >> 8) & 0xFF);
                        section.Data[patch.Offset + 2] = (byte)((absValue >> 16) & 0xFF);
                        section.Data[patch.Offset + 3] = (byte)((absValue >> 24) & 0xFF);
                        break;
                    case PatchKind.Relative8:
                        long absPCAfterInstr = section.PlacedAddress + patch.PCAfterInstruction;
                        long rel = absValue - absPCAfterInstr;
                        if (rel < -128 || rel > 127)
                        {
                            _diagnostics.Report(
                                patch.DiagnosticSpan,
                                $"JR target out of range: offset {rel} does not fit in signed byte"
                            );
                            continue;
                        }
                        section.Data[patch.Offset] = (byte)(sbyte)rel;
                        break;
                    default:
                        _diagnostics.Report(
                            patch.DiagnosticSpan,
                            $"Unhandled patch kind {patch.Kind}"
                        );
                        break;
                }
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
