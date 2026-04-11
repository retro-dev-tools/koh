using Koh.Linker.Core;

namespace Koh.Debugger.Session;

public sealed class BreakpointManager
{
    private readonly HashSet<uint> _execution = [];

    public int Count => _execution.Count;

    public void ClearAll() => _execution.Clear();

    public void Add(BankedAddress address) => _execution.Add(address.Packed);
    public void Remove(BankedAddress address) => _execution.Remove(address.Packed);

    public bool Contains(BankedAddress address) => _execution.Contains(address.Packed);
}
