using System.IO;
using System.Text;

namespace Koh.Linker.Core;

/// <summary>
/// Writes the .kdbg binary format per design §9. Byte-packed, little-endian.
/// Phase 1: no address-map coalescing, no expansion-pool deduplication.
/// </summary>
public static class KdbgFileWriter
{
    public static void Write(Stream output, DebugInfoBuilder builder)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        // Build section payloads into memory to compute offsets.
        byte[] stringPool = BuildStringPool(builder);
        byte[] sourceTable = BuildSourceTable(builder);
        byte[] scopeTable = builder.HasScopeData ? BuildScopeTable(builder) : [];
        byte[] symbolTable = BuildSymbolTable(builder);

        uint headerSize = (uint)KdbgFormat.HeaderSize;
        uint stringPoolOffset = headerSize;
        uint sourceTableOffset = stringPoolOffset + (uint)stringPool.Length;
        uint scopeTableOffset = builder.HasScopeData
            ? sourceTableOffset + (uint)sourceTable.Length
            : 0;
        uint symbolTableOffset = builder.HasScopeData
            ? scopeTableOffset + (uint)scopeTable.Length
            : sourceTableOffset + (uint)sourceTable.Length;
        uint addressMapOffset = symbolTableOffset + (uint)symbolTable.Length;

        int addressMapByteSize = 4 + 16 * builder.AddressMap.Count;
        uint expansionPoolOffset = builder.HasExpansionData
            ? (uint)(addressMapOffset + addressMapByteSize)
            : 0;

        byte[] expansionPool = [];
        uint[] stackIdxToAbsoluteOffset = [];
        if (builder.HasExpansionData)
        {
            (expansionPool, stackIdxToAbsoluteOffset) = BuildExpansionPool(builder, expansionPoolOffset);
        }

        byte[] addressMap = BuildAddressMap(builder, stackIdxToAbsoluteOffset);

        ushort flags = 0;
        if (builder.HasExpansionData) flags |= KdbgFormat.FlagExpansionPresent;
        if (builder.HasScopeData) flags |= KdbgFormat.FlagScopeTablePresent;

        // Header (32 bytes)
        writer.Write(KdbgFormat.Magic);           // 0..3
        writer.Write(KdbgFormat.Version1);        // 4..5
        writer.Write(flags);                      // 6..7
        writer.Write(stringPoolOffset);           // 8..11
        writer.Write(sourceTableOffset);          // 12..15
        writer.Write(scopeTableOffset);           // 16..19
        writer.Write(symbolTableOffset);          // 20..23
        writer.Write(addressMapOffset);           // 24..27
        writer.Write(expansionPoolOffset);        // 28..31

        writer.Write(stringPool);
        writer.Write(sourceTable);
        if (builder.HasScopeData) writer.Write(scopeTable);
        writer.Write(symbolTable);
        writer.Write(addressMap);
        if (builder.HasExpansionData) writer.Write(expansionPool);
    }

    private static byte[] BuildStringPool(DebugInfoBuilder b)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)b.Strings.Count);
        foreach (var s in b.Strings)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > ushort.MaxValue)
                throw new InvalidDataException($".kdbg string too long: {bytes.Length}");
            w.Write((ushort)bytes.Length);
            w.Write(bytes);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildSourceTable(DebugInfoBuilder b)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)b.SourceFiles.Count);
        foreach (var id in b.SourceFiles)
            w.Write(id);
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildScopeTable(DebugInfoBuilder b)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)b.Scopes.Count);
        foreach (var s in b.Scopes)
        {
            w.Write((byte)s.Kind);
            w.Write((byte)0);
            w.Write((ushort)0);
            w.Write(s.ParentScopeId);
            w.Write(s.NameStringId);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildSymbolTable(DebugInfoBuilder b)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)b.Symbols.Count);
        foreach (var s in b.Symbols)
        {
            w.Write((byte)s.Kind);
            w.Write(s.Bank);
            w.Write(s.Address);
            w.Write(s.Size);
            w.Write((ushort)0);
            w.Write(s.NameStringId);
            w.Write(s.ScopeId);
            w.Write(s.DefinitionSourceFileId);
            w.Write(s.DefinitionLine);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildAddressMap(DebugInfoBuilder b, uint[] stackIdxToAbsoluteOffset)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)b.AddressMap.Count);
        foreach (var e in b.AddressMap)
        {
            w.Write(e.Bank);
            w.Write(e.ByteCount);
            w.Write(e.Address);
            w.Write(e.SourceFileId);
            w.Write(e.Line);

            uint expansionOffset = e.ExpansionStackOffset;
            if (expansionOffset != KdbgFormat.NoExpansion)
            {
                expansionOffset = stackIdxToAbsoluteOffset[(int)expansionOffset];
            }
            w.Write(expansionOffset);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static (byte[] bytes, uint[] stackIdxToAbsoluteOffset) BuildExpansionPool(
        DebugInfoBuilder b, uint expansionPoolAbsoluteStart)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        long poolByteSizePos = ms.Position;
        w.Write((uint)0);   // reserve

        var stackIdxToAbsoluteOffset = new uint[b.ExpansionStackIndexes.Count];

        for (int i = 0; i < b.ExpansionStackIndexes.Count; i++)
        {
            int firstFrameIdxInPool = (int)b.ExpansionStackIndexes[i];
            int nextStackFirstIdx = i + 1 < b.ExpansionStackIndexes.Count
                ? (int)b.ExpansionStackIndexes[i + 1]
                : b.ExpansionPool.Count;
            int depth = nextStackFirstIdx - firstFrameIdxInPool;

            uint stackBytePositionInPayload = (uint)(ms.Position - (poolByteSizePos + 4));
            uint absoluteOffsetIntoFile = expansionPoolAbsoluteStart + 4 + stackBytePositionInPayload;
            stackIdxToAbsoluteOffset[i] = absoluteOffsetIntoFile;

            w.Write((ushort)depth);
            for (int j = 0; j < depth; j++)
            {
                var frame = b.ExpansionPool[firstFrameIdxInPool + j];
                w.Write(frame.SourceFileId);
                w.Write(frame.Line);
            }
        }

        long endPos = ms.Position;
        uint payloadSize = (uint)(endPos - (poolByteSizePos + 4));
        ms.Position = poolByteSizePos;
        w.Write(payloadSize);
        ms.Position = endPos;
        w.Flush();

        return (ms.ToArray(), stackIdxToAbsoluteOffset);
    }
}
