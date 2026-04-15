using Koh.Linker.Core;

namespace Koh.Debugger.Session;

public sealed class BreakpointManager
{
    public sealed class BreakpointState
    {
        public string? Condition;
        public int HitCount;
        public int HitCountTarget;   // 0 = always break
    }

    private readonly Dictionary<uint, BreakpointState> _execution = new();

    public int Count => _execution.Count;

    public void ClearAll() => _execution.Clear();

    public void Add(BankedAddress address, string? condition = null, int hitCountTarget = 0)
    {
        _execution[address.Packed] = new BreakpointState
        {
            Condition = condition,
            HitCountTarget = hitCountTarget,
        };
    }

    public void Remove(BankedAddress address) => _execution.Remove(address.Packed);

    public bool Contains(BankedAddress address) => _execution.ContainsKey(address.Packed);

    /// <summary>
    /// Returns true if the breakpoint at <paramref name="address"/> should halt
    /// execution. Tracks hit count internally; evaluates the condition via
    /// <paramref name="evaluateCondition"/> (pass null when no evaluator is
    /// available — conditions then always pass).
    /// </summary>
    public bool ShouldBreak(BankedAddress address, Func<string, bool>? evaluateCondition)
    {
        if (!_execution.TryGetValue(address.Packed, out var state)) return false;
        state.HitCount++;

        if (state.HitCountTarget > 0 && state.HitCount < state.HitCountTarget)
            return false;
        if (state.Condition is { } cond && evaluateCondition is not null && !evaluateCondition(cond))
            return false;

        return true;
    }
}
