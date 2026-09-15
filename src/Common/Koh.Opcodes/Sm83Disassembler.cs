namespace Koh.Opcodes;

/// <summary>Decodes SM83 machine code to text using <see cref="Sm83InstructionTable"/>.</summary>
public static class Sm83Disassembler
{
    private static readonly InstructionDescriptor?[] Unprefixed = new InstructionDescriptor?[256];
    private static readonly InstructionDescriptor?[] CbPrefixed = new InstructionDescriptor?[256];

    static Sm83Disassembler()
    {
        // First entry wins: aliases follow their canonical form in table order.
        foreach (var d in Sm83InstructionTable.All)
        {
            if (d.Encoding[0] == 0xCB)
                CbPrefixed[d.Encoding[1]] ??= d;
            else if (d.EmitRules.Any(r => r.Kind == EmitRuleKind.OpcodeOrImm8))
                for (int vector = 0; vector < 8; vector++)
                    Unprefixed[d.Encoding[0] | (vector << 3)] ??= d;
            else
                Unprefixed[d.Encoding[0]] ??= d;
        }
    }

    /// <summary>
    /// Decodes one instruction at <paramref name="address"/>. Returns its text and byte length;
    /// an illegal opcode is <c>?? $xx</c> with length 1.
    /// </summary>
    public static (string mnemonic, int length) DecodeOne(Func<ushort, byte> read, ushort address)
    {
        byte op = read(address);
        var d = op == 0xCB ? CbPrefixed[read((ushort)(address + 1))] : Unprefixed[op];
        if (d is null)
            return ($"?? ${op:X2}", 1);

        var values = new int[d.Operands.Length];
        int cursor = d.Encoding.Length;
        foreach (var rule in d.EmitRules)
        {
            values[rule.OperandIndex] = rule.Kind switch
            {
                EmitRuleKind.AppendImm16LE => read((ushort)(address + cursor))
                    | (read((ushort)(address + cursor + 1)) << 8),
                EmitRuleKind.OpcodeOrImm8 => op & 0x38,
                _ => read((ushort)(address + cursor)),
            };
            cursor += rule.Kind switch
            {
                EmitRuleKind.AppendImm16LE => 2,
                EmitRuleKind.OpcodeOrImm8 => 0,
                _ => 1,
            };
        }

        if (d.Operands.Length == 0)
            return (d.Mnemonic, d.Size);
        var operands = d.Operands.Select((p, i) => Render(p, values[i], d.ExpectedBitIndex));
        return ($"{d.Mnemonic} {string.Join(",", operands)}", d.Size);
    }

    private static string Render(OperandPattern pattern, int value, int? bit) =>
        pattern switch
        {
            OperandPattern.RegA => "A",
            OperandPattern.RegB => "B",
            OperandPattern.RegC => "C",
            OperandPattern.RegD => "D",
            OperandPattern.RegE => "E",
            OperandPattern.RegH => "H",
            OperandPattern.RegL => "L",
            OperandPattern.RegAF => "AF",
            OperandPattern.RegBC => "BC",
            OperandPattern.RegDE => "DE",
            OperandPattern.RegHL => "HL",
            OperandPattern.RegSP => "SP",
            OperandPattern.IndHL => "(HL)",
            OperandPattern.IndBC => "(BC)",
            OperandPattern.IndDE => "(DE)",
            OperandPattern.IndHLInc => "(HL+)",
            OperandPattern.IndHLDec => "(HL-)",
            OperandPattern.IndC => "($FF00+C)",
            OperandPattern.Imm8 => $"${value:X2}",
            OperandPattern.Imm16 => $"${value:X4}",
            OperandPattern.Imm8Signed => $"{(sbyte)value:+0;-0}",
            OperandPattern.Imm3 => $"{bit}",
            OperandPattern.IndImm8 => $"(${value:X2})",
            OperandPattern.IndImm16 => $"(${value:X4})",
            OperandPattern.CondNZ => "NZ",
            OperandPattern.CondZ => "Z",
            OperandPattern.CondNC => "NC",
            OperandPattern.CondC => "C",
            OperandPattern.RstVec => $"${value:X2}",
            OperandPattern.SpPlusImm8 => $"SP{(sbyte)value:+0;-0}",
            _ => throw new ArgumentOutOfRangeException(nameof(pattern), pattern, null),
        };
}
