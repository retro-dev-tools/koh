using Koh.Linker.Core;

namespace Koh.Debugger.Session;

public sealed class DebugInfoLoader
{
    public SourceMap SourceMap { get; } = new();
    public SymbolMap SymbolMap { get; } = new();

    public void Load(ReadOnlyMemory<byte> kdbgBytes)
    {
        if (kdbgBytes.Length == 0) return;
        var parsed = KdbgReader.Parse(kdbgBytes.ToArray());
        foreach (var sym in parsed.Symbols)
            SymbolMap.Add(sym);
        foreach (var entry in parsed.AddressMap)
        {
            if (entry.SourceFile is null) continue;
            var addr = new BankedAddress(entry.Bank, entry.Address);
            SourceMap.Add(entry.SourceFile, entry.Line, addr);
        }
    }
}
