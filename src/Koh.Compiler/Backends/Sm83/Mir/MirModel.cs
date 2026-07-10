namespace Koh.Compiler.Backends.Sm83.Mir;

/// <summary>
/// The SM83 register file, as a bit set so a read/write footprint is a cheap mask and a register pair
/// decomposes into its two byte halves (<c>BC = B | C</c>). <c>F</c> (flags) is tracked separately by
/// <see cref="Sm83Flags"/>; <c>AF</c> therefore reads/writes <c>A</c> here plus the flag set there.
/// </summary>
[Flags]
public enum Sm83Register : byte
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    C = 1 << 2,
    D = 1 << 3,
    E = 1 << 4,
    H = 1 << 5,
    L = 1 << 6,
    Sp = 1 << 7,

    Bc = B | C,
    De = D | E,
    Hl = H | L,
    All = A | B | C | D | E | H | L | Sp,
}

/// <summary>The four SM83 condition flags, as a bit set for read/write footprints.</summary>
[Flags]
public enum Sm83Flags : byte
{
    None = 0,
    Z = 1 << 0, // zero
    N = 1 << 1, // subtract
    H = 1 << 2, // half-carry
    C = 1 << 3, // carry
    All = Z | N | H | C,
}

/// <summary>How an instruction affects control flow — the information a decoder needs to recover basic
/// block boundaries for liveness and for a superoptimizer's equivalence checking.</summary>
public enum MirControl : byte
{
    /// <summary>Falls through to the next instruction (the common case).</summary>
    Fallthrough,

    /// <summary>Unconditional local jump (<c>JR/JP</c>, <c>JP HL</c>).</summary>
    Jump,

    /// <summary>Conditional jump (<c>JR cc</c> / <c>JP cc</c>) — falls through or jumps.</summary>
    Branch,

    /// <summary>A <c>CALL</c>/<c>RST</c> (conditional or not).</summary>
    Call,

    /// <summary>A <c>RET</c>/<c>RETI</c> (conditional or not).</summary>
    Return,

    /// <summary><c>HALT</c>/<c>STOP</c>.</summary>
    Halt,
}

/// <summary>
/// The complete machine-level effect footprint of one SM83 instruction: which registers and flags it
/// reads and writes, whether it touches memory, and how it steers control flow. This is what the
/// existing byte-buffer peephole approximates by hand and what a superoptimizer needs to check two
/// sequences equivalent; deriving it once, structurally, from the opcode is the point of the MIR layer.
/// </summary>
public readonly record struct MirEffects(
    Sm83Register RegRead,
    Sm83Register RegWrite,
    Sm83Flags FlagRead,
    Sm83Flags FlagWrite,
    bool MemRead,
    bool MemWrite,
    MirControl Control,
    bool SideEffect = false
)
{
    /// <summary>
    /// True when the instruction has an observable effect this footprint does not otherwise capture —
    /// notably toggling the interrupt-master-enable (<c>DI</c>/<c>EI</c>/<c>RETI</c>). Such an
    /// instruction must never be deleted as "dead" or reordered across, even though it writes no
    /// register, flag, or memory a consumer models. A consumer that removes instructions with an empty
    /// read/write footprint must exclude those with <see cref="SideEffect"/> set.
    /// </summary>
    public bool SideEffect { get; init; } = SideEffect;

    /// <summary>A maximally-conservative footprint for an opcode the decoder does not model (an illegal
    /// or unhandled encoding): assume it reads and writes everything, touches memory, and has a side
    /// effect, so any consumer treats it as an opaque barrier rather than reordering across it unsoundly.</summary>
    public static readonly MirEffects Opaque = new(
        Sm83Register.All,
        Sm83Register.All,
        Sm83Flags.All,
        Sm83Flags.All,
        MemRead: true,
        MemWrite: true,
        MirControl.Fallthrough,
        SideEffect: true
    );
}
