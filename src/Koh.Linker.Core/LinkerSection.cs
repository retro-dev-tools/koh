using Koh.Core.Binding;

namespace Koh.Linker.Core;

/// <summary>
/// A section being linked. Carries the section data from one object file
/// plus its placement result after the linker assigns addresses.
/// </summary>
public sealed class LinkerSection
{
    public string Name { get; }
    public SectionType Type { get; }
    public int? FixedAddress { get; }
    public int? Bank { get; }
    public byte[] Data { get; }
    public IReadOnlyList<PatchEntry> Patches { get; }
    public IReadOnlyList<LineMapEntry> LineMap { get; }
    public string SourceFile { get; }

    /// <summary>Assigned by the section placer. -1 if not yet placed.</summary>
    public int PlacedAddress { get; internal set; } = -1;

    /// <summary>Assigned by the section placer.</summary>
    public int PlacedBank { get; internal set; }

    public LinkerSection(SectionData section, string sourceFile)
    {
        Name = section.Name;
        Type = section.Type;
        FixedAddress = section.FixedAddress;
        Bank = section.Bank;
        Data = section.Data;
        Patches = section.Patches;
        LineMap = section.LineMap;
        SourceFile = sourceFile;
    }
}
