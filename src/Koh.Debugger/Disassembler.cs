namespace Koh.Debugger;

/// <summary>
/// Minimal SM83 disassembler — enough to display instructions in the VS Code
/// disassembly view. Not a full mnemonic set yet; unknown opcodes render as
/// the raw byte(s). Enhanced coverage arrives with Phase 3 polish.
/// </summary>
public static class Disassembler
{
    /// <summary>
    /// Decodes one instruction starting at <paramref name="address"/>. Returns
    /// the mnemonic and the byte length of the instruction.
    /// </summary>
    public static (string mnemonic, int length) DecodeOne(Func<ushort, byte> read, ushort address)
    {
        byte op = read(address);

        // Handle CB-prefixed first.
        if (op == 0xCB)
        {
            byte cb = read((ushort)(address + 1));
            return (DecodeCb(cb), 2);
        }

        return DecodeUnprefixed(op, a => read((ushort)(address + a)));
    }

    private static (string, int) DecodeUnprefixed(byte op, Func<int, byte> read)
    {
        // Common 8-bit registers for LD r,r / ALU A,r layouts.
        string[] r8 = { "B", "C", "D", "E", "H", "L", "(HL)", "A" };

        // LD r,r' ($40..$7F) except $76 = HALT.
        if (op >= 0x40 && op <= 0x7F)
        {
            if (op == 0x76) return ("HALT", 1);
            int dst = (op >> 3) & 7;
            int src = op & 7;
            return ($"LD {r8[dst]},{r8[src]}", 1);
        }

        // ALU A,r ($80..$BF).
        if (op >= 0x80 && op <= 0xBF)
        {
            string[] alu = { "ADD A,", "ADC A,", "SUB ", "SBC A,", "AND ", "XOR ", "OR ", "CP " };
            int a = (op >> 3) & 7;
            int s = op & 7;
            return ($"{alu[a]}{r8[s]}", 1);
        }

        return op switch
        {
            0x00 => ("NOP", 1),
            0x01 => ($"LD BC,${ReadU16(read, 1):X4}", 3),
            0x02 => ("LD (BC),A", 1),
            0x03 => ("INC BC", 1),
            0x04 => ("INC B", 1),
            0x05 => ("DEC B", 1),
            0x06 => ($"LD B,${read(1):X2}", 2),
            0x07 => ("RLCA", 1),
            0x08 => ($"LD (${ReadU16(read, 1):X4}),SP", 3),
            0x09 => ("ADD HL,BC", 1),
            0x0A => ("LD A,(BC)", 1),
            0x0B => ("DEC BC", 1),
            0x0C => ("INC C", 1),
            0x0D => ("DEC C", 1),
            0x0E => ($"LD C,${read(1):X2}", 2),
            0x0F => ("RRCA", 1),

            0x10 => ("STOP", 2),
            0x11 => ($"LD DE,${ReadU16(read, 1):X4}", 3),
            0x12 => ("LD (DE),A", 1),
            0x13 => ("INC DE", 1),
            0x17 => ("RLA", 1),
            0x18 => ($"JR {(sbyte)read(1):+0;-0}", 2),
            0x19 => ("ADD HL,DE", 1),
            0x1A => ("LD A,(DE)", 1),
            0x1F => ("RRA", 1),

            0x20 => ($"JR NZ,{(sbyte)read(1):+0;-0}", 2),
            0x21 => ($"LD HL,${ReadU16(read, 1):X4}", 3),
            0x22 => ("LD (HL+),A", 1),
            0x27 => ("DAA", 1),
            0x28 => ($"JR Z,{(sbyte)read(1):+0;-0}", 2),
            0x2A => ("LD A,(HL+)", 1),
            0x2F => ("CPL", 1),

            0x30 => ($"JR NC,{(sbyte)read(1):+0;-0}", 2),
            0x31 => ($"LD SP,${ReadU16(read, 1):X4}", 3),
            0x32 => ("LD (HL-),A", 1),
            0x37 => ("SCF", 1),
            0x38 => ($"JR C,{(sbyte)read(1):+0;-0}", 2),
            0x3A => ("LD A,(HL-)", 1),
            0x3E => ($"LD A,${read(1):X2}", 2),
            0x3F => ("CCF", 1),

            0xC0 => ("RET NZ", 1),
            0xC1 => ("POP BC", 1),
            0xC2 => ($"JP NZ,${ReadU16(read, 1):X4}", 3),
            0xC3 => ($"JP ${ReadU16(read, 1):X4}", 3),
            0xC4 => ($"CALL NZ,${ReadU16(read, 1):X4}", 3),
            0xC5 => ("PUSH BC", 1),
            0xC6 => ($"ADD A,${read(1):X2}", 2),
            0xC8 => ("RET Z", 1),
            0xC9 => ("RET", 1),
            0xCA => ($"JP Z,${ReadU16(read, 1):X4}", 3),
            0xCC => ($"CALL Z,${ReadU16(read, 1):X4}", 3),
            0xCD => ($"CALL ${ReadU16(read, 1):X4}", 3),
            0xCE => ($"ADC A,${read(1):X2}", 2),

            0xD0 => ("RET NC", 1),
            0xD1 => ("POP DE", 1),
            0xD5 => ("PUSH DE", 1),
            0xD6 => ($"SUB ${read(1):X2}", 2),
            0xD9 => ("RETI", 1),
            0xDE => ($"SBC A,${read(1):X2}", 2),

            0xE0 => ($"LDH (${read(1):X2}),A", 2),
            0xE1 => ("POP HL", 1),
            0xE2 => ("LD ($FF00+C),A", 1),
            0xE5 => ("PUSH HL", 1),
            0xE6 => ($"AND ${read(1):X2}", 2),
            0xE8 => ($"ADD SP,{(sbyte)read(1):+0;-0}", 2),
            0xE9 => ("JP (HL)", 1),
            0xEA => ($"LD (${ReadU16(read, 1):X4}),A", 3),
            0xEE => ($"XOR ${read(1):X2}", 2),

            0xF0 => ($"LDH A,(${read(1):X2})", 2),
            0xF1 => ("POP AF", 1),
            0xF2 => ("LD A,($FF00+C)", 1),
            0xF3 => ("DI", 1),
            0xF5 => ("PUSH AF", 1),
            0xF6 => ($"OR ${read(1):X2}", 2),
            0xF8 => ($"LD HL,SP{(sbyte)read(1):+0;-0}", 2),
            0xF9 => ("LD SP,HL", 1),
            0xFA => ($"LD A,(${ReadU16(read, 1):X4})", 3),
            0xFB => ("EI", 1),
            0xFE => ($"CP ${read(1):X2}", 2),

            _ when (op & 0xC7) == 0xC7 => ($"RST ${(op & 0x38):X2}", 1),

            _ => ($"?? ${op:X2}", 1),
        };
    }

    private static string DecodeCb(byte op)
    {
        string[] r8 = { "B", "C", "D", "E", "H", "L", "(HL)", "A" };
        int reg = op & 7;
        int bit = (op >> 3) & 7;
        int group = op >> 6;
        return group switch
        {
            0 => (bit switch
            {
                0 => $"RLC {r8[reg]}",
                1 => $"RRC {r8[reg]}",
                2 => $"RL {r8[reg]}",
                3 => $"RR {r8[reg]}",
                4 => $"SLA {r8[reg]}",
                5 => $"SRA {r8[reg]}",
                6 => $"SWAP {r8[reg]}",
                _ => $"SRL {r8[reg]}",
            }),
            1 => $"BIT {bit},{r8[reg]}",
            2 => $"RES {bit},{r8[reg]}",
            _ => $"SET {bit},{r8[reg]}",
        };
    }

    private static ushort ReadU16(Func<int, byte> read, int offset)
        => (ushort)(read(offset) | (read(offset + 1) << 8));
}
