namespace Koh.Compiler.Backends.Sm83;

/// <summary>
/// The encoded length in bytes of each SM83 opcode — the single source both the byte-buffer peephole
/// (<see cref="Sm83Peephole"/>) and the MIR decoder (<see cref="Mir.MirDecoder"/>) use to recover
/// instruction boundaries, so the table is not duplicated between them. CB-prefixed instructions are
/// always 2 bytes; illegal opcodes (never emitted by the backend) are length 1.
/// </summary>
internal static class Sm83OpcodeLength
{
    /// <summary>Length of the instruction whose first byte is <paramref name="opcode"/>.</summary>
    public static int Of(byte opcode) => opcode == 0xCB ? 2 : Table[opcode];

    // Declared as a span so the compiler emits it as a static blob (no heap array) and elides the
    // bounds check when indexed by a byte.
    // csharpier-ignore
    private static ReadOnlySpan<byte> Table =>
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
}
