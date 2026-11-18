using Koh.Objects;

namespace Koh.Debugger.Session;

public sealed class SymbolMap
{
    private readonly Dictionary<string, DebugSymbol> _byName = new(
        StringComparer.OrdinalIgnoreCase
    );
    private readonly Dictionary<BankedAddress, List<DebugSymbol>> _byAddress = new();

    public void Add(DebugSymbol sym)
    {
        if (sym.Kind != DebugSymbolKind.Constant && sym.Bank > byte.MaxValue)
            throw new InvalidDataException(
                $"Debug bank {sym.Bank} exceeds the emulator's 255-bank limit"
            );
        _byName[sym.Name] = sym;
        if (sym.Kind == DebugSymbolKind.Constant)
            return;
        var addr = new BankedAddress(checked((byte)sym.Bank), sym.Address);
        if (!_byAddress.TryGetValue(addr, out var list))
            _byAddress[addr] = list = new();
        list.Add(sym);
    }

    public DebugSymbol? Lookup(string name) => _byName.TryGetValue(name, out var s) ? s : null;

    public IReadOnlyList<DebugSymbol> LookupByAddress(BankedAddress addr) =>
        _byAddress.TryGetValue(addr, out var list) ? list : Array.Empty<DebugSymbol>();

    public IEnumerable<DebugSymbol> All => _byName.Values;
}
