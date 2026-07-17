namespace Koh.Emulator.Core.Debug;

/// <summary>
/// Fans <see cref="OnRead"/>/<see cref="OnWrite"/> out to a list of inner hooks. <c>Mmu.Hook</c>
/// (<c>src/Koh.Emulator.Core/Bus/Mmu.cs</c>) is a single settable slot, not a list — every hook
/// (<see cref="Mode3WriteGuard"/>, <c>WatchpointHook</c>, ad hoc VRAM recorders) assumes it owns
/// <c>Mmu.Hook</c> outright. A caller that wants to run several hooks at once (e.g. the mode-3 guard
/// alongside an address watchpoint) attaches one <see cref="CompositeMemoryHook"/> instead.
/// </summary>
public sealed class CompositeMemoryHook : MemoryHook
{
    private readonly List<MemoryHook> _hooks;

    public CompositeMemoryHook(params IEnumerable<MemoryHook> hooks) => _hooks = [.. hooks];

    public override void OnRead(ushort address, byte value)
    {
        foreach (var hook in _hooks)
            hook.OnRead(address, value);
    }

    public override void OnWrite(ushort address, byte value)
    {
        foreach (var hook in _hooks)
            hook.OnWrite(address, value);
    }
}
