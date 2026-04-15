namespace Koh.Emulator.Core;

[Flags]
public enum StopConditionKind : uint
{
    None          = 0,
    PcEquals      = 1 << 0,
    PcInRange     = 1 << 1,
    PcLeavesRange = 1 << 2,
    MaxCycles     = 1 << 3,
    VBlank        = 1 << 4,
    Return        = 1 << 5,
}

public readonly struct StopCondition
{
    public StopConditionKind Kind { get; init; }
    public ushort PcEquals { get; init; }
    public ushort PcRangeStart { get; init; }
    public ushort PcRangeEnd { get; init; }
    public ulong MaxTCycles { get; init; }
    public byte BankFilter { get; init; }  // 0xFF = any bank

    public static StopCondition None => default;

    public static StopCondition AtPc(ushort pc, byte bank = 0xFF) => new()
    {
        Kind = StopConditionKind.PcEquals,
        PcEquals = pc,
        BankFilter = bank,
    };

    public static StopCondition WhilePcInRange(ushort start, ushort end, byte bank = 0xFF) => new()
    {
        Kind = StopConditionKind.PcLeavesRange,
        PcRangeStart = start,
        PcRangeEnd = end,
        BankFilter = bank,
    };
}
