namespace Koh.Compiler.Backends.Sm83;

/// <summary>
/// A sound, flag-liveness-aware peephole over an already-emitted SM83 code region. The backend emits
/// a flat byte buffer with no instruction boundaries, so this pass first decodes the region using the
/// fixed SM83 opcode-length table (below), giving real instruction boundaries, then applies rewrites
/// that are only valid when the flags they clobber are dead.
///
/// Currently one rewrite: <c>LD A, 0</c> (<c>3E 00</c>, 2 bytes) → <c>XOR A</c> (<c>AF</c>, 1 byte),
/// saving a byte and a cycle. <c>XOR A</c> clobbers all flags, so it is applied only when a forward
/// scan proves every flag is dead at that point — the scan reaches an instruction that redefines all
/// flags (an ALU op with A that does not itself read a flag) before hitting any flag read or any
/// control-flow boundary. That scan is exactly what makes it safe inside multi-byte <c>ADC</c>/
/// <c>SBC</c> chains: the next byte's <c>ADC</c> reads carry, so the zero-load feeding it is left as
/// <c>LD A, 0</c>.
///
/// The pass reports edits as byte offsets; <see cref="Emitter.PeepholeFrom"/> applies them and
/// relocates the region's labels, fixups, and line map. Boundaries (branch targets, block labels)
/// are passed in so the liveness scan treats them conservatively as "all flags live".
/// </summary>
internal static class Sm83Peephole
{
    // Length in bytes of each unprefixed SM83 opcode (CB-prefixed instructions are always 2). Invalid
    // opcodes (never emitted by this backend) are marked length 1; if one ever appeared, the decode
    // round-trip test on real ROMs would catch the resulting desync.
    // csharpier-ignore
    private static readonly byte[] Length =
    [
        1, 3, 1, 1, 1, 1, 2, 1, 3, 1, 1, 1, 1, 1, 2, 1, // 0x00
        2, 3, 1, 1, 1, 1, 2, 1, 2, 1, 1, 1, 1, 1, 2, 1, // 0x10
        2, 3, 1, 1, 1, 1, 2, 1, 2, 1, 1, 1, 1, 1, 2, 1, // 0x20
        2, 3, 1, 1, 1, 1, 2, 1, 2, 1, 1, 1, 1, 1, 2, 1, // 0x30
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 0x40  LD r,r' / (HL) / HALT
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 0x50
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 0x60
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 0x70
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 0x80  ALU A,r
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 0x90
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 0xA0
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, // 0xB0
        1, 1, 3, 3, 3, 1, 2, 1, 1, 1, 3, 2, 3, 3, 2, 1, // 0xC0
        1, 1, 3, 1, 3, 1, 2, 1, 1, 1, 3, 1, 3, 1, 2, 1, // 0xD0
        2, 1, 1, 1, 1, 1, 2, 1, 2, 1, 3, 1, 1, 1, 2, 1, // 0xE0
        3, 1, 1, 1, 1, 1, 2, 1, 2, 1, 3, 1, 1, 1, 2, 1, // 0xF0
    ];

    /// <summary>Length of the instruction whose opcode is at <paramref name="offset"/>.</summary>
    public static int InstructionLength(IReadOnlyList<byte> code, int offset) =>
        code[offset] == 0xCB ? 2 : Length[code[offset]];

    /// <summary>Positions of <c>LD A, 0</c> instructions in [start, end) that can become <c>XOR A</c>
    /// because all flags are dead at that point.</summary>
    public static List<int> FindZeroLoadEdits(
        IReadOnlyList<byte> code,
        int start,
        int end,
        HashSet<int> boundaries
    )
    {
        // Instruction start offsets, in order — the decode gives real boundaries.
        var starts = new List<int>();
        for (var o = start; o < end; )
        {
            starts.Add(o);
            var len = InstructionLength(code, o);
            if (len <= 0 || o + len > end)
                return []; // decode desync: refuse to edit this region
            o += len;
        }

        var edits = new List<int>();
        for (var i = 0; i < starts.Count; i++)
        {
            var o = starts[i];
            if (code[o] != 0x3E || code[o + 1] != 0x00) // LD A, 0
                continue;
            if (FlagsDeadAfter(code, starts, i, boundaries))
                edits.Add(o);
        }
        return edits;
    }

    /// <summary>True when every CPU flag is dead immediately after instruction <paramref name="index"/>:
    /// scanning forward, an all-flag-redefining ALU op is reached before any flag read or boundary.</summary>
    private static bool FlagsDeadAfter(
        IReadOnlyList<byte> code,
        List<int> starts,
        int index,
        HashSet<int> boundaries
    )
    {
        for (var i = index + 1; i < starts.Count; i++)
        {
            var o = starts[i];
            if (boundaries.Contains(o))
                return false; // a branch target / block join: assume flags live
            var op = code[o];
            if (IsControlFlow(op))
                return false;
            if (ReadsFlag(op))
                return false;
            if (RedefinesAllFlags(op))
                return true;
            // Otherwise the instruction reads no flag and does not redefine all of them; keep scanning.
        }
        return false; // reached region end without a redefinition: be conservative
    }

    private static bool IsControlFlow(byte op) =>
        op
            is 0x18
                or 0x20
                or 0x28
                or 0x30
                or 0x38 // JR / JR cc
                or 0xC3
                or 0xC2
                or 0xCA
                or 0xD2
                or 0xDA
                or 0xE9 // JP / JP cc / JP (HL)
                or 0xCD
                or 0xC4
                or 0xCC
                or 0xD4
                or 0xDC // CALL / CALL cc
                or 0xC9
                or 0xC0
                or 0xC8
                or 0xD0
                or 0xD8
                or 0xD9 // RET / RET cc / RETI
                or 0xC7
                or 0xCF
                or 0xD7
                or 0xDF
                or 0xE7
                or 0xEF
                or 0xF7
                or 0xFF // RST
                or 0x76
                or 0x10; // HALT / STOP (treated as boundaries)

    /// <summary>Instructions that read a flag (so a live flag reaching them is genuinely live). Kept
    /// deliberately broad: any uncertainty errs toward "reads a flag", which only blocks the rewrite.</summary>
    private static bool ReadsFlag(byte op) =>
        op is 0x88 or 0x89 or 0x8A or 0x8B or 0x8C or 0x8D or 0x8E or 0x8F or 0xCE // ADC (reads C)
        || op is 0x98 or 0x99 or 0x9A or 0x9B or 0x9C or 0x9D or 0x9E or 0x9F or 0xDE // SBC (reads C)
        || op is 0x17 or 0x1F // RLA / RRA (read C)
        || op is 0x27 // DAA (reads N/H/C)
        || op is 0x3F // CCF (reads C)
        || op == 0xCB; // any CB-prefixed op: conservatively assume it reads a flag

    /// <summary>ALU ops with A that redefine all four flags without reading one first
    /// (ADD/SUB/AND/OR/XOR/CP, register and immediate forms), plus POP AF which loads F wholesale.</summary>
    private static bool RedefinesAllFlags(byte op) =>
        (op >= 0x80 && op <= 0x87) // ADD A,r
        || (op >= 0x90 && op <= 0x97) // SUB r
        || (op >= 0xA0 && op <= 0xB7) // AND/XOR/OR r  (0xA0..0xB7 spans AND,XOR,OR; ADC/SBC excluded above)
        || (op >= 0xB8 && op <= 0xBF) // CP r
        || op is 0xC6 or 0xD6 or 0xE6 or 0xEE or 0xF6 or 0xFE // ADD/SUB/AND/XOR/OR/CP d8
        || op == 0xF1; // POP AF
}
