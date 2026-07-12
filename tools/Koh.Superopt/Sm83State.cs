namespace Koh.Superopt;

/// <summary>The 8-bit registers, flags, and SP that make up a concrete SM83 machine state — the input the
/// oracle feeds a sequence and (for A..L plus flags) the output it compares. <see cref="Sp"/> is carried
/// so a sequence runs against a realistic stack pointer (probes fix it at the DMG boot value, 0xFFFE),
/// but it is <em>not</em> part of the comparable surface: <see cref="Live"/> has no SP bit, and <see
/// cref="LiveFields"/> never projects it. That makes SP-touching code outside the oracle's domain — the
/// enumerator's alphabet filter (<see cref="Sm83Alphabet.IsStraightLineRegisterOnly"/>) rejects any
/// instruction that reads or writes SP, alongside memory and control-flow ops, so equivalence claims
/// never depend on a value the oracle cannot see change.</summary>
public readonly record struct Sm83State(
    byte A,
    byte F,
    byte B,
    byte C,
    byte D,
    byte E,
    byte H,
    byte L,
    ushort Sp
)
{
    /// <summary>Every <see cref="Live"/> flag paired with the projection of <see cref="Sm83State"/> it
    /// gates. Shared by every walk over live-out fields (comparison, serialization) so the flag-to-field
    /// mapping is defined once. Flags project to the high nibble of F, matching how the SM83 only ever
    /// exposes the top 4 bits.</summary>
    internal static readonly (Live Flag, Func<Sm83State, byte> Get)[] LiveFields =
    [
        (Live.A, s => s.A),
        (Live.B, s => s.B),
        (Live.C, s => s.C),
        (Live.D, s => s.D),
        (Live.E, s => s.E),
        (Live.H, s => s.H),
        (Live.L, s => s.L),
        (Live.Flags, s => (byte)(s.F & 0xF0)),
    ];
}

/// <summary>Which parts of <see cref="Sm83State"/> a caller cares about after a sequence runs — the
/// live-out set. A rewrite need only preserve these, so a smaller live-out admits more (and cheaper)
/// equivalents. Flags are compared as a set (the high nibble of F).</summary>
[Flags]
public enum Live : byte
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    C = 1 << 2,
    D = 1 << 3,
    E = 1 << 4,
    H = 1 << 5,
    L = 1 << 6,
    Flags = 1 << 7,
    AllRegs = A | B | C | D | E | H | L,
    All = AllRegs | Flags,
}
