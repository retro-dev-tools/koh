using Koh.Linker.Core;

namespace Koh.Debugger.Session;

public sealed class SymbolMap
{
    private readonly Dictionary<string, KdbgParsedSymbol> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<BankedAddress, List<KdbgParsedSymbol>> _byAddress = new();

    public void Add(KdbgParsedSymbol sym)
    {
        _byName[sym.Name] = sym;
        var addr = new BankedAddress(sym.Bank, sym.Address);
        if (!_byAddress.TryGetValue(addr, out var list))
            _byAddress[addr] = list = new();
        list.Add(sym);
    }

    public KdbgParsedSymbol? Lookup(string name)
        => _byName.TryGetValue(name, out var s) ? s : null;

    public IReadOnlyList<KdbgParsedSymbol> LookupByAddress(BankedAddress addr)
        => _byAddress.TryGetValue(addr, out var list) ? list : Array.Empty<KdbgParsedSymbol>();

    public IEnumerable<KdbgParsedSymbol> All => _byName.Values;
}
