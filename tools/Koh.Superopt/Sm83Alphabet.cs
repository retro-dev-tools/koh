using Koh.Compiler.Backends.Sm83.Mir;

namespace Koh.Superopt;

/// <summary>
/// The small, straight-line, register-only instruction alphabet the enumerator searches over. Kept
/// deliberately small so enumeration and CI stay fast; each entry is validated against the MIR decoder
/// (<see cref="IsStraightLineRegisterOnly"/>) so membership is a checked property, not a hand assertion.
///
/// ponytail: curated alphabet; widen for deeper manual mining.
/// </summary>
public static class Sm83Alphabet
{
    // A curated straight-line, register-only span: reg/reg moves, accumulator ALU, INC/DEC A, and a
    // couple of immediates. Enough to rediscover canonical peephole wins; widen for deeper manual mining.
    public static IReadOnlyList<byte[]> Ops { get; } =
    [
        [0x00], // NOP
        [0xAF], // XOR A       (A = 0, writes flags)
        [0xB7], // OR A,A      (flags from A, A unchanged)
        [0xA7], // AND A,A     (flags from A, A unchanged)
        [0x3C], // INC A
        [0x3D], // DEC A
        [0x78], // LD A,B
        [0x79], // LD A,C
        [0x47], // LD B,A
        [0x4F], // LD C,A
        [0x7F], // LD A,A
        [0x3E, 0x00], // LD A,0
    ];

    /// <summary>True iff every instruction the region decodes to is memory-free and falls through — the
    /// soundness precondition for the register-state oracle. Uses the shared MIR decoder so the property
    /// is derived from the canonical opcode semantics, not re-encoded here.</summary>
    public static bool IsStraightLineRegisterOnly(ReadOnlySpan<byte> code)
    {
        var program = MirDecoder.Decode(code.ToArray());
        foreach (var instruction in program.Instructions)
        {
            var e = instruction.Effects;
            if (e.MemRead || e.MemWrite || e.Control != MirControl.Fallthrough || e.SideEffect)
                return false;
        }
        return true;
    }
}
