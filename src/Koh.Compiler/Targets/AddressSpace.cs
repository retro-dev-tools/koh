namespace Koh.Compiler.Targets;

/// <summary>
/// Named address spaces carried by IR pointer types. This is the one generalization
/// of "banking" that survives across targets: SM83 distinguishes ROM/WRAM/HRAM/SRAM/VRAM
/// and a banked <see cref="Far"/> space, while register-rich 32-bit targets (GBA/PSX)
/// use only <see cref="Default"/>.
/// </summary>
public enum AddressSpace
{
    /// <summary>Target's flat default space (all a 32-bit target ever needs).</summary>
    Default = 0,
    Rom,
    Wram,
    Hram,
    Sram,
    Vram,

    /// <summary>A banked pointer whose reference crosses banks (far call / far data).</summary>
    Far,
}
