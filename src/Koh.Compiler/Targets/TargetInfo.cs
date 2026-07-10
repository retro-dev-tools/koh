namespace Koh.Compiler.Targets;

/// <summary>
/// Everything a backend needs to know that is target-specific and does not belong in the
/// shared IR: data layout, and (as the backend grows) register file and calling convention.
/// Kept separate from <c>IBackend</c> so target facts can be queried without instantiating
/// code generation.
/// </summary>
/// <param name="Name">Stable identifier, e.g. "sm83", "arm7tdmi".</param>
/// <param name="Layout">How IR types are laid out for this target.</param>
public sealed record TargetInfo(string Name, DataLayout Layout)
{
    /// <summary>The Game Boy / Game Boy Color CPU target.</summary>
    public static TargetInfo Sm83 { get; } = new("sm83", DataLayout.Sm83);
}
