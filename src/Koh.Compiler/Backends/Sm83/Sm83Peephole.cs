using System.Buffers;

namespace Koh.Compiler.Backends.Sm83;

/// <summary>
/// A flag-liveness-aware peephole over an already-emitted SM83 region. The backend emits a flat byte
/// buffer with no instruction boundaries, so this decodes the region via the opcode-length table to
/// recover boundaries, then rewrites <c>LD A, 0</c> to <c>XOR A</c> (1 byte instead of 2). Since
/// <c>XOR A</c> clobbers the flags, it is applied only where a forward scan proves them dead — it
/// reaches a flag-redefining ALU op before any flag read or control-flow boundary. That is what keeps
/// it sound inside <c>ADC</c>/<c>SBC</c> carry chains, where the next byte reads carry.
/// <see cref="Emitter.PeepholeFrom"/> applies the edits and relocates the region's labels/fixups/lines.
/// </summary>
internal static class Sm83Peephole
{
    // Length in bytes of each unprefixed SM83 opcode (CB-prefixed instructions are always 2). Invalid
    // opcodes (never emitted by this backend) are length 1. Declared as a span so the compiler emits it
    // as a static blob (no heap array) and elides the bounds check when indexed by a byte.
    // csharpier-ignore
    private static ReadOnlySpan<byte> Length =>
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
        2, 1, 1, 1, 1, 1, 2, 1, 2, 1, 3, 1, 1, 1, 2, 1, // 0xF0  (F0 = LDH A,(a8), 2 bytes)
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

    // Branches/joins that end a straight-line run: JR/JP/CALL/RET (all condition forms), RST, HALT, STOP.
    // csharpier-ignore
    private static readonly SearchValues<byte> ControlFlow = SearchValues.Create(
    [
        0x18, 0x20, 0x28, 0x30, 0x38, 0xC3, 0xC2, 0xCA, 0xD2, 0xDA, 0xE9, 0xCD, 0xC4, 0xCC, 0xD4,
        0xDC, 0xC9, 0xC0, 0xC8, 0xD0, 0xD8, 0xD9, 0xC7, 0xCF, 0xD7, 0xDF, 0xE7, 0xEF, 0xF7, 0xFF,
        0x76, 0x10,
    ]);

    // Opcodes that read a flag: ADC/SBC/RLA/RRA (C), DAA, CCF, and — conservatively — the CB prefix.
    // csharpier-ignore
    private static readonly SearchValues<byte> FlagReaders = SearchValues.Create(
    [
        0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F, 0xCE, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D,
        0x9E, 0x9F, 0xDE, 0x17, 0x1F, 0x27, 0x3F, 0xCB,
    ]);

    private static bool IsControlFlow(byte op) => ControlFlow.Contains(op);

    private static bool ReadsFlag(byte op) => FlagReaders.Contains(op);

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
