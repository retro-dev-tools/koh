using System.IO;
using System.Text;

namespace Koh.Linker.Core;

public sealed record KdbgParsed(
    IReadOnlyList<KdbgParsedSymbol> Symbols,
    IReadOnlyList<KdbgParsedAddressMapEntry> AddressMap);

public sealed record KdbgParsedSymbol(
    KdbgSymbolKind Kind,
    byte Bank,
    ushort Address,
    ushort Size,
    string Name,
    string? Scope,
    string? DefinitionFile,
    uint DefinitionLine);

public sealed record KdbgParsedAddressMapEntry(
    byte Bank,
    byte ByteCount,
    ushort Address,
    string? SourceFile,
    uint Line,
    IReadOnlyList<(string? File, uint Line)> ExpansionStack);

public static class KdbgReader
{
    public static KdbgParsed Parse(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint magic = r.ReadUInt32();
        if (magic != KdbgFormat.Magic)
            throw new InvalidDataException($"Bad .kdbg magic: 0x{magic:X8}");
        ushort version = r.ReadUInt16();
        if (version != KdbgFormat.Version1)
            throw new InvalidDataException($"Unsupported .kdbg version: {version}");
        ushort flags = r.ReadUInt16();
        uint stringPoolOffset = r.ReadUInt32();
        uint sourceTableOffset = r.ReadUInt32();
        uint scopeTableOffset = r.ReadUInt32();
        uint symbolTableOffset = r.ReadUInt32();
        uint addressMapOffset = r.ReadUInt32();
        uint expansionPoolOffset = r.ReadUInt32();

        // String pool
        ms.Position = stringPoolOffset;
        uint strCount = r.ReadUInt32();
        var strings = new string[strCount + 1];
        strings[0] = "";
        for (int i = 0; i < strCount; i++)
        {
            ushort len = r.ReadUInt16();
            strings[i + 1] = Encoding.UTF8.GetString(r.ReadBytes(len));
        }

        // Source file table
        ms.Position = sourceTableOffset;
        uint srcCount = r.ReadUInt32();
        var sourceFiles = new string?[srcCount + 1];
        sourceFiles[0] = null;
        for (int i = 0; i < srcCount; i++)
            sourceFiles[i + 1] = LookupString(strings, r.ReadUInt32());

        // Scope table (optional)
        string?[] scopeNames;
        if ((flags & KdbgFormat.FlagScopeTablePresent) != 0)
        {
            ms.Position = scopeTableOffset;
            uint scopeCount = r.ReadUInt32();
            scopeNames = new string?[scopeCount + 1];
            scopeNames[0] = null;
            for (int i = 0; i < scopeCount; i++)
            {
                r.ReadByte();
                r.ReadByte(); r.ReadUInt16();
                r.ReadUInt32();
                uint nameStringId = r.ReadUInt32();
                scopeNames[i + 1] = LookupString(strings, nameStringId);
            }
        }
        else
        {
            scopeNames = [null];
        }

        // Symbol table
        ms.Position = symbolTableOffset;
        uint symCount = r.ReadUInt32();
        var symbols = new KdbgParsedSymbol[symCount];
        for (int i = 0; i < symCount; i++)
        {
            var kind = (KdbgSymbolKind)r.ReadByte();
            byte bank = r.ReadByte();
            ushort address = r.ReadUInt16();
            ushort size = r.ReadUInt16();
            r.ReadUInt16();
            uint nameStringId = r.ReadUInt32();
            uint scopeId = r.ReadUInt32();
            uint defSourceFileId = r.ReadUInt32();
            uint defLine = r.ReadUInt32();
            symbols[i] = new KdbgParsedSymbol(
                kind, bank, address, size,
                LookupString(strings, nameStringId) ?? "",
                scopeId < scopeNames.Length ? scopeNames[scopeId] : null,
                defSourceFileId < sourceFiles.Length ? sourceFiles[defSourceFileId] : null,
                defLine);
        }

        // Address map
        ms.Position = addressMapOffset;
        uint amCount = r.ReadUInt32();
        var addressMap = new KdbgParsedAddressMapEntry[amCount];
        for (int i = 0; i < amCount; i++)
        {
            byte bank = r.ReadByte();
            byte byteCount = r.ReadByte();
            ushort address = r.ReadUInt16();
            uint sourceFileId = r.ReadUInt32();
            uint line = r.ReadUInt32();
            uint expansionOffset = r.ReadUInt32();

            IReadOnlyList<(string?, uint)> expansionStack = [];
            if (expansionOffset != KdbgFormat.NoExpansion &&
                (flags & KdbgFormat.FlagExpansionPresent) != 0)
            {
                long mark = ms.Position;
                ms.Position = expansionOffset;
                ushort depth = r.ReadUInt16();
                var stack = new (string?, uint)[depth];
                for (int k = 0; k < depth; k++)
                {
                    uint fid = r.ReadUInt32();
                    uint fline = r.ReadUInt32();
                    stack[k] = (fid < sourceFiles.Length ? sourceFiles[fid] : null, fline);
                }
                expansionStack = stack;
                ms.Position = mark;
            }

            addressMap[i] = new KdbgParsedAddressMapEntry(
                bank, byteCount, address,
                sourceFileId < sourceFiles.Length ? sourceFiles[sourceFileId] : null,
                line, expansionStack);
        }

        return new KdbgParsed(symbols, addressMap);
    }

    private static string? LookupString(string[] strings, uint id)
        => id == 0 ? null : (id < strings.Length ? strings[id] : null);
}
