using Koh.Core.Binding;
using Koh.Core.Symbols;
using Koh.Core.Syntax;

namespace Koh.Emit;

/// <summary>
/// Reads a .kobj binary stream back into an EmitModel.
/// </summary>
public sealed class KobjReader
{
    public static EmitModel Read(Stream stream)
    {
        using var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // Header
        Span<byte> magic = stackalloc byte[4];
        br.ReadExactly(magic);
        if (!magic.SequenceEqual(KobjFormat.Magic))
            throw new InvalidDataException("Not a valid .kobj file (bad magic)");

        var version = br.ReadByte();
        if (version != KobjFormat.Version)
            throw new InvalidDataException($"Unsupported .kobj version: {version}");

        var sections = new List<SectionData>();
        var symbols = new List<SymbolData>();

        while (true)
        {
            var tag = br.ReadByte();
            switch (tag)
            {
                case KobjFormat.TagSections:
                    ReadSections(br, sections);
                    break;
                case KobjFormat.TagSymbols:
                    ReadSymbols(br, symbols);
                    break;
                case KobjFormat.TagEnd:
                    // Diagnostics are not stored in .kobj. The writer guarantees it only
                    // writes files from successful compilations, so success is always true here.
                    return new EmitModel(sections, symbols, success: true);
                default:
                    throw new InvalidDataException($"Unknown .kobj tag: 0x{tag:X2}");
            }
        }
    }

    private static void ReadSections(BinaryReader br, List<SectionData> sections)
    {
        var count = br.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            var name = br.ReadString();
            var type = (SectionType)br.ReadByte();
            int? fixedAddress = br.ReadBoolean() ? br.ReadInt32() : null;
            int? bank = br.ReadBoolean() ? br.ReadInt32() : null;
            var dataLen = br.ReadInt32();
            var data = br.ReadBytes(dataLen);

            var patchCount = br.ReadUInt16();
            var patches = new List<PatchEntry>(patchCount);
            for (int p = 0; p < patchCount; p++)
            {
                var offset = br.ReadInt32();
                var kind = (PatchKind)br.ReadByte();
                var pcAfter = br.ReadInt32();
                var spanStart = br.ReadInt32();
                var spanLen = br.ReadInt32();
                patches.Add(new PatchEntry
                {
                    SectionName = name,
                    Offset = offset,
                    Expression = null, // Expression not serialized — linker re-evaluates from source
                    Kind = kind,
                    PCAfterInstruction = pcAfter,
                    DiagnosticSpan = new TextSpan(spanStart, spanLen),
                });
            }

            sections.Add(new SectionData(name, type, fixedAddress, bank, data, patches));
        }
    }

    private static void ReadSymbols(BinaryReader br, List<SymbolData> symbols)
    {
        var count = br.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            var name = br.ReadString();
            var kind = (SymbolKind)br.ReadByte();
            var vis = (SymbolVisibility)br.ReadByte();
            var section = br.ReadString();
            var value = br.ReadInt64();
            symbols.Add(new SymbolData(name, kind, vis,
                string.IsNullOrEmpty(section) ? null : section, value));
        }
    }
}
