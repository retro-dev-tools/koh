using Koh.Compiler.Backends.Sm83.Mir;

namespace Koh.Compiler.Backends.Sm83;

/// <summary>
/// A peephole over an already-emitted SM83 region, driven by the <see cref="MirDecoder"/>: the region
/// is lifted to typed instructions with a <see cref="MirEffects"/> footprint each, and rewrites are
/// found by reading those footprints rather than hand-maintained opcode sets. Two rewrites, both of the
/// shape "two bytes become one" (overwrite the first byte, delete the second), which is what
/// <see cref="Emitter.PeepholeFrom"/> applies:
/// <list type="bullet">
/// <item><c>LD A, 0</c> → <c>XOR A</c> — one byte shorter, but <c>XOR A</c> clobbers the flags, so it is
/// applied only where <see cref="FlagsDeadAfter"/> proves every flag dead (a forward scan reaches an
/// instruction that rewrites all flags before any flag is read or a boundary is hit — sound inside
/// <c>ADC</c>/<c>SBC</c> carry chains, where the next op reads carry).</item>
/// <item><c>LD A,(HL)</c>/<c>LD (HL),A</c> immediately followed by <c>INC HL</c>/<c>DEC HL</c> →
/// the auto-increment/decrement form (<c>LD A,(HL+)</c> etc.), folding the pointer bump into the load or
/// store. Neither instruction touches a flag, so the only guard is that the <c>INC</c>/<c>DEC HL</c> is
/// not a branch target.</item>
/// </list>
/// </summary>
internal static class Sm83Peephole
{
    /// <summary>One rewrite: overwrite the byte at <see cref="Offset"/> with <see cref="NewOpcode"/> and
    /// delete the byte at <c>Offset + 1</c>. Both current rules collapse a two-byte sequence into the
    /// single opcode, so this shape covers them.</summary>
    public readonly record struct Edit(int Offset, byte NewOpcode);

    /// <summary>Length of the instruction whose opcode is at <paramref name="offset"/>. Delegates to
    /// the shared <see cref="Sm83OpcodeLength"/> table so the length data is not duplicated.</summary>
    public static int InstructionLength(IReadOnlyList<byte> code, int offset) =>
        Sm83OpcodeLength.Of(code[offset]);

    /// <summary>The rewrites applicable in <c>[start, end)</c>, in ascending offset order.
    /// <paramref name="boundaries"/> holds the absolute offsets of branch targets / block joins, across
    /// which liveness cannot be assumed and instructions must not be folded away.</summary>
    public static List<Edit> FindEdits(
        IReadOnlyList<byte> code,
        int start,
        int end,
        HashSet<int> boundaries
    )
    {
        // Lift the region to typed instructions. Offsets in `instrs` are relative to `start`; the shared
        // decoder recovers boundaries and computes each instruction's effect footprint.
        var region = new byte[end - start];
        for (var i = 0; i < region.Length; i++)
            region[i] = code[start + i];
        var instrs = MirDecoder.Decode(region).Instructions;

        var edits = new List<Edit>();
        for (var i = 0; i < instrs.Count; i++)
        {
            var instr = instrs[i];
            var abs = start + instr.Offset;

            // Rule 1 — LD A, 0 → XOR A, when the flags XOR A would clobber are dead.
            if (IsLoadAZero(instr))
            {
                if (FlagsDeadAfter(instrs, i, start, boundaries))
                    edits.Add(new Edit(abs, 0xAF));
                continue;
            }

            // Rule 2 — fold a following INC/DEC HL into an (HL) load/store. The INC/DEC HL must be the
            // next instruction and not itself a branch target.
            if (
                i + 1 < instrs.Count
                && !boundaries.Contains(start + instrs[i + 1].Offset)
                && TryFoldHlStep(instr.Opcode, instrs[i + 1].Opcode) is { } folded
            )
            {
                edits.Add(new Edit(abs, folded));
                i++; // the INC/DEC HL is consumed by the fold — don't reconsider it
            }
        }
        return edits;
    }

    private static bool IsLoadAZero(MirInstruction instr) =>
        instr is { Opcode: 0x3E, Length: 2 } && instr.Bytes[1] == 0x00;

    /// <summary>The auto-increment/decrement opcode that folds <paramref name="load"/> (an <c>(HL)</c>
    /// accumulator load/store) with <paramref name="step"/> (<c>INC HL</c> 0x23 / <c>DEC HL</c> 0x2B),
    /// or null if the pair is not foldable.</summary>
    private static byte? TryFoldHlStep(byte load, byte step) =>
        (load, step) switch
        {
            (0x7E, 0x23) => 0x2A, // LD A,(HL) ; INC HL → LD A,(HL+)
            (0x7E, 0x2B) => 0x3A, // LD A,(HL) ; DEC HL → LD A,(HL-)
            (0x77, 0x23) => 0x22, // LD (HL),A ; INC HL → LD (HL+),A
            (0x77, 0x2B) => 0x32, // LD (HL),A ; DEC HL → LD (HL-),A
            _ => null,
        };

    /// <summary>True when every CPU flag is dead immediately after instruction <paramref name="index"/>:
    /// scanning forward, an instruction that rewrites all four flags is reached before any flag is read
    /// or a boundary is hit. Reads the decoded <see cref="MirEffects"/> instead of hand-rolled opcode
    /// sets, so it is exact where the old scan was conservative (e.g. a CB rotate that reads no carry but
    /// rewrites all flags now proves the flags dead).</summary>
    private static bool FlagsDeadAfter(
        IReadOnlyList<MirInstruction> instrs,
        int index,
        int start,
        HashSet<int> boundaries
    )
    {
        for (var i = index + 1; i < instrs.Count; i++)
        {
            var instr = instrs[i];
            if (boundaries.Contains(start + instr.Offset))
                return false; // a branch target / join: assume flags live
            var e = instr.Effects;
            if (e.Control != MirControl.Fallthrough)
                return false; // a branch/call/return/halt ends the straight-line run
            if (e.FlagRead != Sm83Flags.None)
                return false; // a flag is consumed before being fully redefined
            if (e.FlagWrite == Sm83Flags.All)
                return true; // all four flags overwritten without a read: dead
        }
        return false; // reached the region end without a full redefinition: be conservative
    }
}
