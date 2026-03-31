namespace Koh.Core.Encoding;

/// <summary>
/// Complete SM83 instruction encoding table. Single source of truth for validation,
/// sizing, and byte emission.
/// </summary>
public static class Sm83InstructionTable
{
    private static readonly ILookup<string, InstructionDescriptor> Table =
        BuildTable().ToLookup(d => d.Mnemonic, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<InstructionDescriptor> Lookup(string mnemonic) => Table[mnemonic];

    private static IEnumerable<InstructionDescriptor> BuildTable()
    {
        // =====================================================================
        // NOP, HALT, STOP, DI, EI, etc. — zero-operand instructions
        // =====================================================================
        yield return I("NOP", [], [0x00], 1);
        yield return I("HALT", [], [0x76], 1);
        yield return I("STOP", [], [0x10, 0x00], 2);
        yield return I("DI", [], [0xF3], 1);
        yield return I("EI", [], [0xFB], 1);
        yield return I("DAA", [], [0x27], 1);
        yield return I("CPL", [], [0x2F], 1);
        yield return I("CCF", [], [0x3F], 1);
        yield return I("SCF", [], [0x37], 1);
        yield return I("RLCA", [], [0x07], 1);
        yield return I("RLA", [], [0x17], 1);
        yield return I("RRCA", [], [0x0F], 1);
        yield return I("RRA", [], [0x1F], 1);
        yield return I("RETI", [], [0xD9], 1);

        // =====================================================================
        // LD r, r' — register to register (8-bit)
        // Base opcode: 0x40 | (dst << 3) | src
        // =====================================================================
        var regs8 = new[] {
            (OperandPattern.RegB, 0), (OperandPattern.RegC, 1),
            (OperandPattern.RegD, 2), (OperandPattern.RegE, 3),
            (OperandPattern.RegH, 4), (OperandPattern.RegL, 5),
            (OperandPattern.RegA, 7),
        };

        foreach (var (dst, dstCode) in regs8)
        foreach (var (src, srcCode) in regs8)
        {
            yield return I("LD", [dst, src], [(byte)(0x40 | (dstCode << 3) | srcCode)], 1);
        }

        // LD r, [HL]
        foreach (var (reg, code) in regs8)
            yield return I("LD", [reg, OperandPattern.IndHL], [(byte)(0x46 | (code << 3))], 1);

        // LD [HL], r
        foreach (var (reg, code) in regs8)
            yield return I("LD", [OperandPattern.IndHL, reg], [(byte)(0x70 | code)], 1);

        // LD r, n (8-bit immediate)
        foreach (var (reg, code) in regs8)
            yield return I("LD", [reg, OperandPattern.Imm8], [(byte)(0x06 | (code << 3))], 2,
                [new(EmitRuleKind.AppendImm8, 1)]);

        // LD [HL], n
        yield return I("LD", [OperandPattern.IndHL, OperandPattern.Imm8], [0x36], 2,
            [new(EmitRuleKind.AppendImm8, 1)]);

        // =====================================================================
        // LD rr, nn — 16-bit register pair load
        // =====================================================================
        yield return I("LD", [OperandPattern.RegBC, OperandPattern.Imm16], [0x01], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);
        yield return I("LD", [OperandPattern.RegDE, OperandPattern.Imm16], [0x11], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);
        yield return I("LD", [OperandPattern.RegHL, OperandPattern.Imm16], [0x21], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);
        yield return I("LD", [OperandPattern.RegSP, OperandPattern.Imm16], [0x31], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);

        // LD SP, HL
        yield return I("LD", [OperandPattern.RegSP, OperandPattern.RegHL], [0xF9], 1);

        // LD HL, SP+r8
        yield return I("LD", [OperandPattern.RegHL, OperandPattern.SpPlusImm8], [0xF8], 2,
            [new(EmitRuleKind.AppendImm8, 1)]);

        // =====================================================================
        // LD — indirect loads
        // =====================================================================
        yield return I("LD", [OperandPattern.RegA, OperandPattern.IndBC], [0x0A], 1);
        yield return I("LD", [OperandPattern.RegA, OperandPattern.IndDE], [0x1A], 1);
        yield return I("LD", [OperandPattern.IndBC, OperandPattern.RegA], [0x02], 1);
        yield return I("LD", [OperandPattern.IndDE, OperandPattern.RegA], [0x12], 1);
        yield return I("LD", [OperandPattern.RegA, OperandPattern.IndHLInc], [0x2A], 1);
        yield return I("LD", [OperandPattern.RegA, OperandPattern.IndHLDec], [0x3A], 1);
        yield return I("LD", [OperandPattern.IndHLInc, OperandPattern.RegA], [0x22], 1);
        yield return I("LD", [OperandPattern.IndHLDec, OperandPattern.RegA], [0x32], 1);

        // LD A, [nn]  /  LD [nn], A
        yield return I("LD", [OperandPattern.RegA, OperandPattern.IndImm16], [0xFA], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);
        yield return I("LD", [OperandPattern.IndImm16, OperandPattern.RegA], [0xEA], 3,
            [new(EmitRuleKind.AppendImm16LE, 0)]);

        // LD [nn], SP
        yield return I("LD", [OperandPattern.IndImm16, OperandPattern.RegSP], [0x08], 3,
            [new(EmitRuleKind.AppendImm16LE, 0)]);

        // =====================================================================
        // LDH — high-page loads
        // =====================================================================
        yield return I("LDH", [OperandPattern.RegA, OperandPattern.IndImm8], [0xF0], 2,
            [new(EmitRuleKind.AppendImm8, 1)]);
        yield return I("LDH", [OperandPattern.IndImm8, OperandPattern.RegA], [0xE0], 2,
            [new(EmitRuleKind.AppendImm8, 0)]);
        yield return I("LDH", [OperandPattern.RegA, OperandPattern.IndC], [0xF2], 1);
        yield return I("LDH", [OperandPattern.IndC, OperandPattern.RegA], [0xE2], 1);
        // LD [$FF00+C], A and LD A, [$FF00+C] are RGBDS synonyms for LDH [C], A / LDH A, [C]
        yield return I("LD", [OperandPattern.RegA, OperandPattern.IndC], [0xF2], 1);
        yield return I("LD", [OperandPattern.IndC, OperandPattern.RegA], [0xE2], 1);

        // LDI / LDD aliases
        yield return I("LDI", [OperandPattern.RegA, OperandPattern.IndHL], [0x2A], 1);
        yield return I("LDI", [OperandPattern.IndHL, OperandPattern.RegA], [0x22], 1);
        yield return I("LDD", [OperandPattern.RegA, OperandPattern.IndHL], [0x3A], 1);
        yield return I("LDD", [OperandPattern.IndHL, OperandPattern.RegA], [0x32], 1);

        // =====================================================================
        // Arithmetic/logic — ALU operations on A
        // =====================================================================
        var aluOps = new[] {
            ("ADD", 0x80), ("ADC", 0x88), ("SUB", 0x90), ("SBC", 0x98),
            ("AND", 0xA0), ("XOR", 0xA8), ("OR", 0xB0), ("CP", 0xB8),
        };

        // ALU immediate opcodes: ADD=0xC6, ADC=0xCE, SUB=0xD6, SBC=0xDE,
        //                        AND=0xE6, XOR=0xEE, OR=0xF6, CP=0xFE
        var aluImmOps = new[] {
            ("ADD", 0xC6), ("ADC", 0xCE), ("SUB", 0xD6), ("SBC", 0xDE),
            ("AND", 0xE6), ("XOR", 0xEE), ("OR", 0xF6), ("CP", 0xFE),
        };

        for (int i = 0; i < aluOps.Length; i++)
        {
            var (mnem, baseOp) = aluOps[i];
            var (_, immOp) = aluImmOps[i];

            // ALU A, r
            foreach (var (reg, code) in regs8)
                yield return I(mnem, [OperandPattern.RegA, reg], [(byte)(baseOp | code)], 1);

            // ALU A, [HL]
            yield return I(mnem, [OperandPattern.RegA, OperandPattern.IndHL], [(byte)(baseOp | 6)], 1);

            // ALU A, n (immediate)
            yield return I(mnem, [OperandPattern.RegA, OperandPattern.Imm8], [(byte)immOp], 2,
                [new(EmitRuleKind.AppendImm8, 1)]);

            // Single-operand shorthand: ALU r → ALU A, r (A is implied)
            foreach (var (reg, code) in regs8)
                yield return I(mnem, [reg], [(byte)(baseOp | code)], 1);

            // ALU [HL] → ALU A, [HL]
            yield return I(mnem, [OperandPattern.IndHL], [(byte)(baseOp | 6)], 1);

            // ALU n → ALU A, n
            yield return I(mnem, [OperandPattern.Imm8], [(byte)immOp], 2,
                [new(EmitRuleKind.AppendImm8, 0)]);
        }

        // =====================================================================
        // INC / DEC — 8-bit
        // =====================================================================
        foreach (var (reg, code) in regs8)
        {
            yield return I("INC", [reg], [(byte)(0x04 | (code << 3))], 1);
            yield return I("DEC", [reg], [(byte)(0x05 | (code << 3))], 1);
        }
        yield return I("INC", [OperandPattern.IndHL], [0x34], 1);
        yield return I("DEC", [OperandPattern.IndHL], [0x35], 1);

        // INC / DEC — 16-bit
        yield return I("INC", [OperandPattern.RegBC], [0x03], 1);
        yield return I("INC", [OperandPattern.RegDE], [0x13], 1);
        yield return I("INC", [OperandPattern.RegHL], [0x23], 1);
        yield return I("INC", [OperandPattern.RegSP], [0x33], 1);
        yield return I("DEC", [OperandPattern.RegBC], [0x0B], 1);
        yield return I("DEC", [OperandPattern.RegDE], [0x1B], 1);
        yield return I("DEC", [OperandPattern.RegHL], [0x2B], 1);
        yield return I("DEC", [OperandPattern.RegSP], [0x3B], 1);

        // =====================================================================
        // ADD HL, rr
        // =====================================================================
        yield return I("ADD", [OperandPattern.RegHL, OperandPattern.RegBC], [0x09], 1);
        yield return I("ADD", [OperandPattern.RegHL, OperandPattern.RegDE], [0x19], 1);
        yield return I("ADD", [OperandPattern.RegHL, OperandPattern.RegHL], [0x29], 1);
        yield return I("ADD", [OperandPattern.RegHL, OperandPattern.RegSP], [0x39], 1);

        // ADD SP, r8
        yield return I("ADD", [OperandPattern.RegSP, OperandPattern.Imm8Signed], [0xE8], 2,
            [new(EmitRuleKind.AppendImm8, 1)]);

        // =====================================================================
        // PUSH / POP
        // =====================================================================
        yield return I("PUSH", [OperandPattern.RegBC], [0xC5], 1);
        yield return I("PUSH", [OperandPattern.RegDE], [0xD5], 1);
        yield return I("PUSH", [OperandPattern.RegHL], [0xE5], 1);
        yield return I("PUSH", [OperandPattern.RegAF], [0xF5], 1);
        yield return I("POP", [OperandPattern.RegBC], [0xC1], 1);
        yield return I("POP", [OperandPattern.RegDE], [0xD1], 1);
        yield return I("POP", [OperandPattern.RegHL], [0xE1], 1);
        yield return I("POP", [OperandPattern.RegAF], [0xF1], 1);

        // =====================================================================
        // JP, JR, CALL, RET, RST
        // =====================================================================
        yield return I("JP", [OperandPattern.Imm16], [0xC3], 3,
            [new(EmitRuleKind.AppendImm16LE, 0)]);
        yield return I("JP", [OperandPattern.CondNZ, OperandPattern.Imm16], [0xC2], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);
        yield return I("JP", [OperandPattern.CondZ, OperandPattern.Imm16], [0xCA], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);
        yield return I("JP", [OperandPattern.CondNC, OperandPattern.Imm16], [0xD2], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);
        yield return I("JP", [OperandPattern.CondC, OperandPattern.Imm16], [0xDA], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);
        yield return I("JP", [OperandPattern.IndHL], [0xE9], 1);
        yield return I("JP", [OperandPattern.RegHL], [0xE9], 1); // jp hl = jp [hl]

        yield return I("JR", [OperandPattern.Imm8Signed], [0x18], 2,
            [new(EmitRuleKind.AppendRelative8, 0)]);
        yield return I("JR", [OperandPattern.CondNZ, OperandPattern.Imm8Signed], [0x20], 2,
            [new(EmitRuleKind.AppendRelative8, 1)]);
        yield return I("JR", [OperandPattern.CondZ, OperandPattern.Imm8Signed], [0x28], 2,
            [new(EmitRuleKind.AppendRelative8, 1)]);
        yield return I("JR", [OperandPattern.CondNC, OperandPattern.Imm8Signed], [0x30], 2,
            [new(EmitRuleKind.AppendRelative8, 1)]);
        yield return I("JR", [OperandPattern.CondC, OperandPattern.Imm8Signed], [0x38], 2,
            [new(EmitRuleKind.AppendRelative8, 1)]);

        yield return I("CALL", [OperandPattern.Imm16], [0xCD], 3,
            [new(EmitRuleKind.AppendImm16LE, 0)]);
        yield return I("CALL", [OperandPattern.CondNZ, OperandPattern.Imm16], [0xC4], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);
        yield return I("CALL", [OperandPattern.CondZ, OperandPattern.Imm16], [0xCC], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);
        yield return I("CALL", [OperandPattern.CondNC, OperandPattern.Imm16], [0xD4], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);
        yield return I("CALL", [OperandPattern.CondC, OperandPattern.Imm16], [0xDC], 3,
            [new(EmitRuleKind.AppendImm16LE, 1)]);

        yield return I("RET", [], [0xC9], 1);
        yield return I("RET", [OperandPattern.CondNZ], [0xC0], 1);
        yield return I("RET", [OperandPattern.CondZ], [0xC8], 1);
        yield return I("RET", [OperandPattern.CondNC], [0xD0], 1);
        yield return I("RET", [OperandPattern.CondC], [0xD8], 1);

        yield return I("RST", [OperandPattern.RstVec], [0xC7], 1,
            [new(EmitRuleKind.OpcodeOrImm8, 0)]);

        // =====================================================================
        // CB-prefix: rotates, shifts, bit ops
        // =====================================================================
        var cbRegs = new[] {
            (OperandPattern.RegB, 0), (OperandPattern.RegC, 1),
            (OperandPattern.RegD, 2), (OperandPattern.RegE, 3),
            (OperandPattern.RegH, 4), (OperandPattern.RegL, 5),
            (OperandPattern.IndHL, 6), (OperandPattern.RegA, 7),
        };

        var cbOps = new[] {
            ("RLC", 0x00), ("RRC", 0x08), ("RL", 0x10), ("RR", 0x18),
            ("SLA", 0x20), ("SRA", 0x28), ("SWAP", 0x30), ("SRL", 0x38),
        };

        foreach (var (mnem, baseOp) in cbOps)
        foreach (var (reg, code) in cbRegs)
        {
            yield return I(mnem, [reg], [0xCB, (byte)(baseOp | code)], 2);
        }

        // BIT b, r / SET b, r / RES b, r
        // Each entry encodes a specific bit index (0–7) in the opcode. ExpectedBitIndex
        // is set so the pattern matcher can reject entries whose bit does not match the
        // source operand value — otherwise "BIT 3, A" would silently emit "BIT 0, A".
        var bitOps = new[] { ("BIT", 0x40), ("RES", 0x80), ("SET", 0xC0) };
        foreach (var (mnem, baseOp) in bitOps)
        foreach (var (reg, code) in cbRegs)
        for (int bit = 0; bit < 8; bit++)
        {
            yield return I(mnem, [OperandPattern.Imm3, reg],
                [0xCB, (byte)(baseOp | (bit << 3) | code)], 2,
                expectedBitIndex: bit);
        }
    }

    private static InstructionDescriptor I(string mnemonic, OperandPattern[] operands,
        byte[] encoding, int size, EmitRule[]? emitRules = null, int? expectedBitIndex = null)
    {
        return new InstructionDescriptor
        {
            Mnemonic = mnemonic,
            Operands = operands,
            Encoding = encoding,
            Size = size,
            EmitRules = emitRules ?? [],
            ExpectedBitIndex = expectedBitIndex,
        };
    }
}
