using Koh.Native;
using Koh.Objects;

namespace Koh.Debugger.Session;

public sealed class DebugInfoLoader
{
    public SourceMap SourceMap { get; private set; } = new();
    public SymbolMap SymbolMap { get; private set; } = new();

    public void Load(ReadOnlyMemory<byte> debugBytes)
    {
        var parsed = debugBytes.IsEmpty
            ? new DebugData([], [])
            : new NativeLinker().ReadDebugInfo(debugBytes);
        var symbols = new SymbolMap();
        var sources = new SourceMap();
        foreach (var sym in parsed.Symbols)
            symbols.Add(sym);
        foreach (var entry in parsed.AddressMap)
        {
            if (entry.Bank > byte.MaxValue)
                throw new InvalidDataException(
                    $"Debug bank {entry.Bank} exceeds the emulator's 255-bank limit"
                );
            var addr = new BankedAddress(checked((byte)entry.Bank), entry.Address);
            sources.Add(entry.SourceFile, entry.Line, addr, entry.ByteCount);
        }
        SymbolMap = symbols;
        SourceMap = sources;
    }
}
