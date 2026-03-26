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
    public bool Success { get; }

    internal LinkResult(byte[]? romData, IReadOnlyList<LinkerSymbol> symbols,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        RomData = romData;
        Symbols = symbols;
        Diagnostics = diagnostics;

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

        return new LinkResult(rom, resolver.AllSymbols, _diagnostics.ToList());
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
                // TODO: cross-file references (calls to symbols defined in a
                // different .kobj) require expression serialisation in both
                // KobjWriter and KobjReader. Until that is implemented, a non-null
                // expression means the in-memory EmitModel was linked directly
                // without a .kobj round-trip. Promote it to an error so the gap
                // is visible rather than silently producing a corrupt ROM.
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
