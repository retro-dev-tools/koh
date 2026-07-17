namespace Koh.Emulator.Core;

[Flags]
public enum StopConditionKind : uint
{
    None = 0,
    PcEquals = 1 << 0,
    PcInRange = 1 << 1,
    PcLeavesRange = 1 << 2,
    MaxCycles = 1 << 3,
    VBlank = 1 << 4,
    Return = 1 << 5,
    Spinning = 1 << 6,
}

public readonly struct StopCondition
{
    /// <summary>
    /// A genuine terminal spin (<c>jr $</c>, or a <c>while(true){}</c> compiled to a 2-4
    /// instruction tight loop) revisits a tiny handful of addresses; anything larger is very
    /// likely a real (if repetitive) loop with a body, like <c>Tilemap.Clear</c>'s 1024-iteration
    /// copy loop, whose body visits many more than 4 distinct addresses per iteration even though
    /// it also repeats thousands of times.
    /// </summary>
    public const int DefaultSpinPcSetSize = 4;

    /// <summary>
    /// Large enough that a legitimate loop with a body under ~4 addresses (rare, but possible)
    /// gets thousands of iterations before being called "spinning", while still resolving in well
    /// under one frame's worth of instructions for a true tight spin.
    /// </summary>
    public const int DefaultSpinStableInstructions = 4096;

    public StopConditionKind Kind { get; init; }
    public ushort PcEquals { get; init; }
    public ushort PcRangeStart { get; init; }
    public ushort PcRangeEnd { get; init; }
    public ulong MaxTCycles { get; init; }
    public byte BankFilter { get; init; } // 0xFF = any bank

    /// <summary>0 =&gt; use <see cref="DefaultSpinPcSetSize"/>.</summary>
    public int SpinPcSetSize { get; init; }

    /// <summary>0 =&gt; use <see cref="DefaultSpinStableInstructions"/>.</summary>
    public int SpinStableInstructions { get; init; }

    public static StopCondition None => default;

    public static StopCondition AtPc(ushort pc, byte bank = 0xFF) =>
        new()
        {
            Kind = StopConditionKind.PcEquals,
            PcEquals = pc,
            BankFilter = bank,
        };

    public static StopCondition WhilePcInRange(ushort start, ushort end, byte bank = 0xFF) =>
        new()
        {
            Kind = StopConditionKind.PcLeavesRange,
            PcRangeStart = start,
            PcRangeEnd = end,
            BankFilter = bank,
        };

    /// <summary>
    /// Convenience factory that always pairs <see cref="StopConditionKind.Spinning"/> with
    /// <see cref="StopConditionKind.MaxCycles"/>, so the ergonomic path can never be an
    /// uncapped cross-frame loop — a caller whose program never spins (by this detector's
    /// narrow definition) and never hits a PC condition would otherwise loop forever.
    /// </summary>
    public static StopCondition SpinningOrBudget(ulong maxTCycles) =>
        new()
        {
            Kind = StopConditionKind.Spinning | StopConditionKind.MaxCycles,
            MaxTCycles = maxTCycles,
        };
}
