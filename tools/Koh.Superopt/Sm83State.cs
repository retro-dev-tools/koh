namespace Koh.Superopt;

/// <summary>The 8-bit registers plus flags and SP — the observable machine state a straight-line,
/// memory-free SM83 sequence can read or write. Enough to define input states and compare outputs for
/// the equivalence oracle. Memory is deliberately out of scope; the enumerator rejects any sequence that
/// touches it (see <see cref="Sm83Alphabet"/>), so the register file is the whole observable state.</summary>
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
);

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
