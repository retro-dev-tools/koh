using System.Collections.Generic;

namespace Koh.Linker.Core;

public enum KdbgSymbolKind : byte
{
    Label = 0,
    EquConstant = 1,
    RamLabel = 2,
    Macro = 3,
    Export = 4,
}

public enum KdbgScopeKind : byte
{
    Global = 0,
    LocalToLabel = 1,
    MacroLocal = 2,
    File = 3,
}

public sealed class DebugInfoBuilder
{
    private readonly List<string> _strings = [];
    private readonly Dictionary<string, uint> _stringIndex = new(StringComparer.Ordinal);

    private readonly List<uint> _sourceFiles = [];
    private readonly Dictionary<string, uint> _sourceFileIndex = new(StringComparer.Ordinal);

    private readonly List<ScopeRecord> _scopes = [];
    private readonly List<SymbolRecord> _symbols = [];
    private readonly List<AddressMapRecord> _addressMap = [];
    private readonly List<ExpansionFrameRecord> _expansionPool = [];
    private readonly List<uint> _expansionStackIndexes = [];

    internal readonly record struct ScopeRecord(KdbgScopeKind Kind, uint ParentScopeId, uint NameStringId);
    internal readonly record struct SymbolRecord(
        KdbgSymbolKind Kind, byte Bank, ushort Address, ushort Size,
        uint NameStringId, uint ScopeId, uint DefinitionSourceFileId, uint DefinitionLine);
    internal readonly record struct AddressMapRecord(
        byte Bank, byte ByteCount, ushort Address,
        uint SourceFileId, uint Line, uint ExpansionStackOffset);
    internal readonly record struct ExpansionFrameRecord(uint SourceFileId, uint Line);

    /// <summary>Intern a string. Returns a 1-based ID; 0 is the "no string" sentinel.</summary>
    public uint InternString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        if (_stringIndex.TryGetValue(value, out var existing)) return existing;
        _strings.Add(value);
        uint id = (uint)_strings.Count;   // 1-based
        _stringIndex[value] = id;
        return id;
    }

    /// <summary>Intern a source file path. Returns a 1-based ID; 0 = "no file".</summary>
    public uint InternSourceFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        if (_sourceFileIndex.TryGetValue(path, out var existing)) return existing;
        uint pathStringId = InternString(path);
        _sourceFiles.Add(pathStringId);
        uint id = (uint)_sourceFiles.Count;
        _sourceFileIndex[path] = id;
        return id;
    }

    /// <summary>Intern a scope. Returns a 1-based ID; 0 = global scope sentinel.</summary>
    public uint InternScope(KdbgScopeKind kind, uint parentScopeId, string? name)
    {
        _scopes.Add(new ScopeRecord(kind, parentScopeId, InternString(name)));
        return (uint)_scopes.Count;
    }

    public void AddSymbol(KdbgSymbolKind kind, byte bank, ushort address, ushort size,
                          string name, uint scopeId, string? definitionSourceFile, uint definitionLine)
    {
        _symbols.Add(new SymbolRecord(
            kind, bank, address, size,
            InternString(name), scopeId,
            InternSourceFile(definitionSourceFile), definitionLine));
    }

    public void AddAddressMapping(byte bank, ushort address, byte byteCount,
                                   string sourceFile, uint line,
                                   IReadOnlyList<(string SourceFile, uint Line)>? expansionStack = null)
    {
        uint fileId = InternSourceFile(sourceFile);
        uint expansionOffset = KdbgFormat.NoExpansion;

        if (expansionStack is { Count: > 0 })
        {
            // Store the index into _expansionStackIndexes. At write time, this index
            // is translated into a file-absolute byte offset into the expansion pool.
            expansionOffset = (uint)_expansionStackIndexes.Count;
            _expansionStackIndexes.Add((uint)_expansionPool.Count);
            foreach (var frame in expansionStack)
                _expansionPool.Add(new ExpansionFrameRecord(InternSourceFile(frame.SourceFile), frame.Line));
        }

        _addressMap.Add(new AddressMapRecord(bank, byteCount, address, fileId, line, expansionOffset));
    }

    public bool HasExpansionData => _expansionStackIndexes.Count > 0;
    public bool HasScopeData => _scopes.Count > 0;

    internal IReadOnlyList<string> Strings => _strings;
    internal IReadOnlyList<uint> SourceFiles => _sourceFiles;
    internal IReadOnlyList<ScopeRecord> Scopes => _scopes;
    internal IReadOnlyList<SymbolRecord> Symbols => _symbols;
    internal List<AddressMapRecord> AddressMap => _addressMap;
    internal IReadOnlyList<ExpansionFrameRecord> ExpansionPool => _expansionPool;
    internal IReadOnlyList<uint> ExpansionStackIndexes => _expansionStackIndexes;
}
