using Koh.Opcodes;

namespace Koh.Superopt;

/// <summary>Register-only search alphabet.</summary>
public static class Sm83Alphabet
{
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

    private static readonly Dictionary<int, int> RegisterInstructions = Sm83InstructionTable
        .All.Where(d =>
            d.Mnemonic
                is "NOP"
                    or "LD"
                    or "INC"
                    or "DEC"
                    or "ADD"
                    or "ADC"
                    or "SUB"
                    or "SBC"
                    or "AND"
                    or "XOR"
                    or "OR"
                    or "CP"
                    or "DAA"
                    or "CPL"
                    or "CCF"
                    or "SCF"
                    or "RLCA"
                    or "RLA"
                    or "RRCA"
                    or "RRA"
                    or "RLC"
                    or "RRC"
                    or "RL"
                    or "RR"
                    or "SLA"
                    or "SRA"
                    or "SWAP"
                    or "SRL"
                    or "BIT"
                    or "RES"
                    or "SET"
            && d.Operands.All(p =>
                p
                    is >= OperandPattern.RegA
                        and <= OperandPattern.RegHL
                        or OperandPattern.Imm8
                        or OperandPattern.Imm16
                        or OperandPattern.Imm3
            )
        )
        .GroupBy(d => d.Encoding[0] == 0xCB ? 0xCB00 | d.Encoding[1] : d.Encoding[0])
        .ToDictionary(g => g.Key, g => g.First().Size);

    /// <summary>Accept complete instructions confined to the oracle's observed state.</summary>
    public static bool IsStraightLineRegisterOnly(ReadOnlySpan<byte> code)
    {
        for (var offset = 0; offset < code.Length; )
        {
            var opcode = (int)code[offset];
            if (opcode == 0xCB)
            {
                if (code.Length - offset < 2)
                    return false;
                opcode = 0xCB00 | code[offset + 1];
            }
            if (
                !RegisterInstructions.TryGetValue(opcode, out var size)
                || size > code.Length - offset
            )
                return false;
            offset += size;
        }
        return true;
    }
}
