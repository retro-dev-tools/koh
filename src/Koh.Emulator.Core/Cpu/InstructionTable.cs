namespace Koh.Emulator.Core.Cpu;

/// <summary>
/// SM83 unprefixed + CB-prefixed instruction decoder table. Each handler mutates
/// registers via <see cref="CpuRegisters"/> and accesses memory through
/// <see cref="IInstructionBus"/>, returning the T-cycle cost of the instruction.
/// </summary>
public static class InstructionTable
{
    public delegate int InstructionHandler(ref CpuRegisters regs, IInstructionBus bus);

    public interface IInstructionBus
    {
        /// <summary>Reads one byte; takes 1 M-cycle (4 T-cycles).</summary>
        byte ReadByte(ushort address);
        /// <summary>Writes one byte; takes 1 M-cycle.</summary>
        void WriteByte(ushort address, byte value);
        /// <summary>Reads byte at PC; takes 1 M-cycle; increments PC.</summary>
        byte ReadImmediate();
        /// <summary>Reads 2 bytes at PC; takes 2 M-cycles.</summary>
        ushort ReadImmediate16();
        /// <summary>
        /// Consume one M-cycle of internal (non-memory) processing. Used for
        /// 16-bit INC/DEC, ADD HL,rr, stack-pointer math, conditional-branch
        /// taken paths, CALL/RET/RST internal cycles, and similar per-opcode
        /// latencies.
        /// </summary>
        void InternalCycle();
        void SetIme(bool enable);
        void Halt();
        void Stop();
    }

    public static readonly InstructionHandler?[] Unprefixed = BuildUnprefixedTable();
    public static readonly InstructionHandler?[] CbPrefixed = BuildCbPrefixedTable();

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    private static byte ReadReg8(ref CpuRegisters r, IInstructionBus bus, int idx) => idx switch
    {
        0 => r.B, 1 => r.C, 2 => r.D, 3 => r.E,
        4 => r.H, 5 => r.L, 6 => bus.ReadByte(r.HL),
        _ => r.A,
    };

    private static void WriteReg8(ref CpuRegisters r, IInstructionBus bus, int idx, byte value)
    {
        switch (idx)
        {
            case 0: r.B = value; break;
            case 1: r.C = value; break;
            case 2: r.D = value; break;
            case 3: r.E = value; break;
            case 4: r.H = value; break;
            case 5: r.L = value; break;
            case 6: bus.WriteByte(r.HL, value); break;
            default: r.A = value; break;
        }
    }

    private static void Push16(ref CpuRegisters r, IInstructionBus bus, ushort value)
    {
        r.Sp = (ushort)(r.Sp - 1);
        bus.WriteByte(r.Sp, (byte)(value >> 8));
        r.Sp = (ushort)(r.Sp - 1);
        bus.WriteByte(r.Sp, (byte)(value & 0xFF));
    }

    private static ushort Pop16(ref CpuRegisters r, IInstructionBus bus)
    {
        byte lo = bus.ReadByte(r.Sp); r.Sp = (ushort)(r.Sp + 1);
        byte hi = bus.ReadByte(r.Sp); r.Sp = (ushort)(r.Sp + 1);
        return (ushort)((hi << 8) | lo);
    }

    // ─────────────────────────────────────────────────────────────
    // ALU
    // ─────────────────────────────────────────────────────────────

    private static void AluAdd(ref CpuRegisters r, byte value, bool withCarry)
    {
        int c = withCarry && r.FlagSet(CpuRegisters.FlagC) ? 1 : 0;
        int result = r.A + value + c;
        int halfCarry = (r.A & 0x0F) + (value & 0x0F) + c;
        r.SetFlag(CpuRegisters.FlagZ, (result & 0xFF) == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, halfCarry > 0x0F);
        r.SetFlag(CpuRegisters.FlagC, result > 0xFF);
        r.A = (byte)result;
    }

    private static void AluSub(ref CpuRegisters r, byte value, bool withCarry, bool storeResult)
    {
        int c = withCarry && r.FlagSet(CpuRegisters.FlagC) ? 1 : 0;
        int result = r.A - value - c;
        int halfCarry = (r.A & 0x0F) - (value & 0x0F) - c;
        r.SetFlag(CpuRegisters.FlagZ, (result & 0xFF) == 0);
        r.SetFlag(CpuRegisters.FlagN, true);
        r.SetFlag(CpuRegisters.FlagH, halfCarry < 0);
        r.SetFlag(CpuRegisters.FlagC, result < 0);
        if (storeResult) r.A = (byte)result;
    }

    private static void AluAnd(ref CpuRegisters r, byte value)
    {
        r.A &= value;
        r.SetFlag(CpuRegisters.FlagZ, r.A == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, true);
        r.SetFlag(CpuRegisters.FlagC, false);
    }

    private static void AluOr(ref CpuRegisters r, byte value)
    {
        r.A |= value;
        r.SetFlag(CpuRegisters.FlagZ, r.A == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, false);
    }

    private static void AluXor(ref CpuRegisters r, byte value)
    {
        r.A ^= value;
        r.SetFlag(CpuRegisters.FlagZ, r.A == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, false);
        r.SetFlag(CpuRegisters.FlagC, false);
    }

    private static byte AluInc(ref CpuRegisters r, byte value)
    {
        byte result = (byte)(value + 1);
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, (value & 0x0F) == 0x0F);
        return result;
    }

    private static byte AluDec(ref CpuRegisters r, byte value)
    {
        byte result = (byte)(value - 1);
        r.SetFlag(CpuRegisters.FlagZ, result == 0);
        r.SetFlag(CpuRegisters.FlagN, true);
        r.SetFlag(CpuRegisters.FlagH, (value & 0x0F) == 0x00);
        return result;
    }

    private static void AluAddHl(ref CpuRegisters r, ushort value)
    {
        int result = r.HL + value;
        int halfCarry = (r.HL & 0x0FFF) + (value & 0x0FFF);
        r.SetFlag(CpuRegisters.FlagN, false);
        r.SetFlag(CpuRegisters.FlagH, halfCarry > 0x0FFF);
        r.SetFlag(CpuRegisters.FlagC, result > 0xFFFF);
        r.HL = (ushort)result;
    }

    private static bool TestCond(ref CpuRegisters r, int cond) => cond switch
    {
        0 => !r.FlagSet(CpuRegisters.FlagZ),
        1 => r.FlagSet(CpuRegisters.FlagZ),
        2 => !r.FlagSet(CpuRegisters.FlagC),
        _ => r.FlagSet(CpuRegisters.FlagC),
    };

    // ─────────────────────────────────────────────────────────────
    // Unprefixed table
    // ─────────────────────────────────────────────────────────────

    private static InstructionHandler?[] BuildUnprefixedTable()
    {
        var t = new InstructionHandler?[256];

        // NOP
        t[0x00] = (ref CpuRegisters r, IInstructionBus bus) => 4;

        // LD rr,d16
        t[0x01] = (ref CpuRegisters r, IInstructionBus bus) => { r.BC = bus.ReadImmediate16(); return 12; };
        t[0x11] = (ref CpuRegisters r, IInstructionBus bus) => { r.DE = bus.ReadImmediate16(); return 12; };
        t[0x21] = (ref CpuRegisters r, IInstructionBus bus) => { r.HL = bus.ReadImmediate16(); return 12; };
        t[0x31] = (ref CpuRegisters r, IInstructionBus bus) => { r.Sp = bus.ReadImmediate16(); return 12; };

        // LD (rr),A / LD A,(rr)
        t[0x02] = (ref CpuRegisters r, IInstructionBus bus) => { bus.WriteByte(r.BC, r.A); return 8; };
        t[0x12] = (ref CpuRegisters r, IInstructionBus bus) => { bus.WriteByte(r.DE, r.A); return 8; };
        t[0x0A] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = bus.ReadByte(r.BC); return 8; };
        t[0x1A] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = bus.ReadByte(r.DE); return 8; };
        t[0x22] = (ref CpuRegisters r, IInstructionBus bus) => { bus.WriteByte(r.HL, r.A); r.HL = (ushort)(r.HL + 1); return 8; };
        t[0x32] = (ref CpuRegisters r, IInstructionBus bus) => { bus.WriteByte(r.HL, r.A); r.HL = (ushort)(r.HL - 1); return 8; };
        t[0x2A] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = bus.ReadByte(r.HL); r.HL = (ushort)(r.HL + 1); return 8; };
        t[0x3A] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = bus.ReadByte(r.HL); r.HL = (ushort)(r.HL - 1); return 8; };

        // INC/DEC 16-bit — 2 M-cycles: fetch + 1 internal (address-math latency).
        t[0x03] = (ref CpuRegisters r, IInstructionBus bus) => { r.BC = (ushort)(r.BC + 1); bus.InternalCycle(); return 8; };
        t[0x13] = (ref CpuRegisters r, IInstructionBus bus) => { r.DE = (ushort)(r.DE + 1); bus.InternalCycle(); return 8; };
        t[0x23] = (ref CpuRegisters r, IInstructionBus bus) => { r.HL = (ushort)(r.HL + 1); bus.InternalCycle(); return 8; };
        t[0x33] = (ref CpuRegisters r, IInstructionBus bus) => { r.Sp = (ushort)(r.Sp + 1); bus.InternalCycle(); return 8; };
        t[0x0B] = (ref CpuRegisters r, IInstructionBus bus) => { r.BC = (ushort)(r.BC - 1); bus.InternalCycle(); return 8; };
        t[0x1B] = (ref CpuRegisters r, IInstructionBus bus) => { r.DE = (ushort)(r.DE - 1); bus.InternalCycle(); return 8; };
        t[0x2B] = (ref CpuRegisters r, IInstructionBus bus) => { r.HL = (ushort)(r.HL - 1); bus.InternalCycle(); return 8; };
        t[0x3B] = (ref CpuRegisters r, IInstructionBus bus) => { r.Sp = (ushort)(r.Sp - 1); bus.InternalCycle(); return 8; };

        // INC/DEC 8-bit (table reg order: B, C, D, E, H, L, (HL), A)
        t[0x04] = (ref CpuRegisters r, IInstructionBus bus) => { r.B = AluInc(ref r, r.B); return 4; };
        t[0x0C] = (ref CpuRegisters r, IInstructionBus bus) => { r.C = AluInc(ref r, r.C); return 4; };
        t[0x14] = (ref CpuRegisters r, IInstructionBus bus) => { r.D = AluInc(ref r, r.D); return 4; };
        t[0x1C] = (ref CpuRegisters r, IInstructionBus bus) => { r.E = AluInc(ref r, r.E); return 4; };
        t[0x24] = (ref CpuRegisters r, IInstructionBus bus) => { r.H = AluInc(ref r, r.H); return 4; };
        t[0x2C] = (ref CpuRegisters r, IInstructionBus bus) => { r.L = AluInc(ref r, r.L); return 4; };
        t[0x34] = (ref CpuRegisters r, IInstructionBus bus) => { byte v = AluInc(ref r, bus.ReadByte(r.HL)); bus.WriteByte(r.HL, v); return 12; };
        t[0x3C] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = AluInc(ref r, r.A); return 4; };

        t[0x05] = (ref CpuRegisters r, IInstructionBus bus) => { r.B = AluDec(ref r, r.B); return 4; };
        t[0x0D] = (ref CpuRegisters r, IInstructionBus bus) => { r.C = AluDec(ref r, r.C); return 4; };
        t[0x15] = (ref CpuRegisters r, IInstructionBus bus) => { r.D = AluDec(ref r, r.D); return 4; };
        t[0x1D] = (ref CpuRegisters r, IInstructionBus bus) => { r.E = AluDec(ref r, r.E); return 4; };
        t[0x25] = (ref CpuRegisters r, IInstructionBus bus) => { r.H = AluDec(ref r, r.H); return 4; };
        t[0x2D] = (ref CpuRegisters r, IInstructionBus bus) => { r.L = AluDec(ref r, r.L); return 4; };
        t[0x35] = (ref CpuRegisters r, IInstructionBus bus) => { byte v = AluDec(ref r, bus.ReadByte(r.HL)); bus.WriteByte(r.HL, v); return 12; };
        t[0x3D] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = AluDec(ref r, r.A); return 4; };

        // LD r,d8
        t[0x06] = (ref CpuRegisters r, IInstructionBus bus) => { r.B = bus.ReadImmediate(); return 8; };
        t[0x0E] = (ref CpuRegisters r, IInstructionBus bus) => { r.C = bus.ReadImmediate(); return 8; };
        t[0x16] = (ref CpuRegisters r, IInstructionBus bus) => { r.D = bus.ReadImmediate(); return 8; };
        t[0x1E] = (ref CpuRegisters r, IInstructionBus bus) => { r.E = bus.ReadImmediate(); return 8; };
        t[0x26] = (ref CpuRegisters r, IInstructionBus bus) => { r.H = bus.ReadImmediate(); return 8; };
        t[0x2E] = (ref CpuRegisters r, IInstructionBus bus) => { r.L = bus.ReadImmediate(); return 8; };
        t[0x36] = (ref CpuRegisters r, IInstructionBus bus) => { bus.WriteByte(r.HL, bus.ReadImmediate()); return 12; };
        t[0x3E] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = bus.ReadImmediate(); return 8; };

        // RLCA / RLA / RRCA / RRA
        t[0x07] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            byte v = r.A;
            byte carry = (byte)((v >> 7) & 1);
            r.A = (byte)((v << 1) | carry);
            r.F = (byte)(carry != 0 ? CpuRegisters.FlagC : 0);
            return 4;
        };
        t[0x17] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            byte oldC = r.FlagSet(CpuRegisters.FlagC) ? (byte)1 : (byte)0;
            byte newC = (byte)((r.A >> 7) & 1);
            r.A = (byte)((r.A << 1) | oldC);
            r.F = (byte)(newC != 0 ? CpuRegisters.FlagC : 0);
            return 4;
        };
        t[0x0F] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            byte carry = (byte)(r.A & 1);
            r.A = (byte)((r.A >> 1) | (carry << 7));
            r.F = (byte)(carry != 0 ? CpuRegisters.FlagC : 0);
            return 4;
        };
        t[0x1F] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            byte oldC = r.FlagSet(CpuRegisters.FlagC) ? (byte)1 : (byte)0;
            byte newC = (byte)(r.A & 1);
            r.A = (byte)((r.A >> 1) | (oldC << 7));
            r.F = (byte)(newC != 0 ? CpuRegisters.FlagC : 0);
            return 4;
        };

        // LD (a16),SP
        t[0x08] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            ushort addr = bus.ReadImmediate16();
            bus.WriteByte(addr, (byte)(r.Sp & 0xFF));
            bus.WriteByte((ushort)(addr + 1), (byte)(r.Sp >> 8));
            return 20;
        };

        // ADD HL,rr — 2 M-cycles: fetch + 1 internal (16-bit ALU latency).
        t[0x09] = (ref CpuRegisters r, IInstructionBus bus) => { AluAddHl(ref r, r.BC); bus.InternalCycle(); return 8; };
        t[0x19] = (ref CpuRegisters r, IInstructionBus bus) => { AluAddHl(ref r, r.DE); bus.InternalCycle(); return 8; };
        t[0x29] = (ref CpuRegisters r, IInstructionBus bus) => { AluAddHl(ref r, r.HL); bus.InternalCycle(); return 8; };
        t[0x39] = (ref CpuRegisters r, IInstructionBus bus) => { AluAddHl(ref r, r.Sp); bus.InternalCycle(); return 8; };

        // STOP. The CGB speed switch uses this. Real STOP behaviour is quirky;
        // we consume the second byte as an immediate fetch and set the Stop flag.
        t[0x10] = (ref CpuRegisters r, IInstructionBus bus) => { bus.ReadImmediate(); bus.Stop(); return 4; };

        // JR r8 — 3 M-cycles: fetch + imm + 1 internal (branch latency).
        t[0x18] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            sbyte offset = (sbyte)bus.ReadImmediate();
            r.Pc = (ushort)(r.Pc + offset);
            bus.InternalCycle();
            return 12;
        };
        // JR cc,r8 — 3 M-cycles if taken, 2 if not. Internal cycle only on taken.
        for (int i = 0; i < 4; i++)
        {
            int cc = i;
            int opcode = 0x20 + cc * 8;
            t[opcode] = (ref CpuRegisters r, IInstructionBus bus) =>
            {
                sbyte offset = (sbyte)bus.ReadImmediate();
                if (TestCond(ref r, cc))
                {
                    r.Pc = (ushort)(r.Pc + offset);
                    bus.InternalCycle();
                    return 12;
                }
                return 8;
            };
        }

        // DAA
        t[0x27] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            int a = r.A;
            if (!r.FlagSet(CpuRegisters.FlagN))
            {
                if (r.FlagSet(CpuRegisters.FlagC) || a > 0x99) { a += 0x60; r.SetFlag(CpuRegisters.FlagC, true); }
                if (r.FlagSet(CpuRegisters.FlagH) || (a & 0x0F) > 0x09) { a += 0x06; }
            }
            else
            {
                if (r.FlagSet(CpuRegisters.FlagC)) a -= 0x60;
                if (r.FlagSet(CpuRegisters.FlagH)) a -= 0x06;
            }
            r.A = (byte)a;
            r.SetFlag(CpuRegisters.FlagZ, r.A == 0);
            r.SetFlag(CpuRegisters.FlagH, false);
            return 4;
        };

        // CPL
        t[0x2F] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            r.A = (byte)~r.A;
            r.SetFlag(CpuRegisters.FlagN, true);
            r.SetFlag(CpuRegisters.FlagH, true);
            return 4;
        };

        // SCF
        t[0x37] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            r.SetFlag(CpuRegisters.FlagN, false);
            r.SetFlag(CpuRegisters.FlagH, false);
            r.SetFlag(CpuRegisters.FlagC, true);
            return 4;
        };

        // CCF
        t[0x3F] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            r.SetFlag(CpuRegisters.FlagN, false);
            r.SetFlag(CpuRegisters.FlagH, false);
            r.SetFlag(CpuRegisters.FlagC, !r.FlagSet(CpuRegisters.FlagC));
            return 4;
        };

        // LD r,r' ($40..$7F) — $76 is HALT
        for (int op = 0x40; op <= 0x7F; op++)
        {
            if (op == 0x76)
            {
                t[op] = (ref CpuRegisters r, IInstructionBus bus) => { bus.Halt(); return 4; };
                continue;
            }
            int dst = (op >> 3) & 7;
            int src = op & 7;
            int opCap = op;
            t[opCap] = (ref CpuRegisters r, IInstructionBus bus) =>
            {
                byte v = ReadReg8(ref r, bus, src);
                WriteReg8(ref r, bus, dst, v);
                return (dst == 6 || src == 6) ? 8 : 4;
            };
        }

        // ALU A,r ($80..$BF)
        for (int op = 0x80; op <= 0xBF; op++)
        {
            int alu = (op >> 3) & 7;
            int src = op & 7;
            int opCap = op;
            t[opCap] = (ref CpuRegisters r, IInstructionBus bus) =>
            {
                byte v = ReadReg8(ref r, bus, src);
                switch (alu)
                {
                    case 0: AluAdd(ref r, v, false); break;
                    case 1: AluAdd(ref r, v, true); break;
                    case 2: AluSub(ref r, v, false, true); break;
                    case 3: AluSub(ref r, v, true, true); break;
                    case 4: AluAnd(ref r, v); break;
                    case 5: AluXor(ref r, v); break;
                    case 6: AluOr(ref r, v); break;
                    case 7: AluSub(ref r, v, false, false); break;  // CP
                }
                return src == 6 ? 8 : 4;
            };
        }

        // RET cc — 5 M-cycles if taken (fetch + internal + 2 reads + internal),
        //           2 M-cycles if not (fetch + internal).
        for (int i = 0; i < 4; i++)
        {
            int cc = i;
            int opcode = 0xC0 + cc * 8;
            t[opcode] = (ref CpuRegisters r, IInstructionBus bus) =>
            {
                bus.InternalCycle();  // cc test latency
                if (TestCond(ref r, cc))
                {
                    r.Pc = Pop16(ref r, bus);
                    bus.InternalCycle();  // PC load latency
                    return 20;
                }
                return 8;
            };
        }

        // POP rr — 3 M-cycles: fetch + 2 reads (stack).
        t[0xC1] = (ref CpuRegisters r, IInstructionBus bus) => { r.BC = Pop16(ref r, bus); return 12; };
        t[0xD1] = (ref CpuRegisters r, IInstructionBus bus) => { r.DE = Pop16(ref r, bus); return 12; };
        t[0xE1] = (ref CpuRegisters r, IInstructionBus bus) => { r.HL = Pop16(ref r, bus); return 12; };
        t[0xF1] = (ref CpuRegisters r, IInstructionBus bus) => { r.AF = Pop16(ref r, bus); return 12; };

        // PUSH rr — 4 M-cycles: fetch + 1 internal (SP dec) + 2 writes.
        t[0xC5] = (ref CpuRegisters r, IInstructionBus bus) => { bus.InternalCycle(); Push16(ref r, bus, r.BC); return 16; };
        t[0xD5] = (ref CpuRegisters r, IInstructionBus bus) => { bus.InternalCycle(); Push16(ref r, bus, r.DE); return 16; };
        t[0xE5] = (ref CpuRegisters r, IInstructionBus bus) => { bus.InternalCycle(); Push16(ref r, bus, r.HL); return 16; };
        t[0xF5] = (ref CpuRegisters r, IInstructionBus bus) => { bus.InternalCycle(); Push16(ref r, bus, r.AF); return 16; };

        // JP cc,a16 — 4 M-cycles if taken (fetch + 2 imm + internal), 3 if not.
        for (int i = 0; i < 4; i++)
        {
            int cc = i;
            int opcode = 0xC2 + cc * 8;
            t[opcode] = (ref CpuRegisters r, IInstructionBus bus) =>
            {
                ushort target = bus.ReadImmediate16();
                if (TestCond(ref r, cc)) { r.Pc = target; bus.InternalCycle(); return 16; }
                return 12;
            };
        }

        // JP a16 — 4 M-cycles: fetch + 2 imm + 1 internal (PC load).
        t[0xC3] = (ref CpuRegisters r, IInstructionBus bus) => { r.Pc = bus.ReadImmediate16(); bus.InternalCycle(); return 16; };

        // CALL cc,a16 — 6 M-cycles if taken, 3 if not.
        for (int i = 0; i < 4; i++)
        {
            int cc = i;
            int opcode = 0xC4 + cc * 8;
            t[opcode] = (ref CpuRegisters r, IInstructionBus bus) =>
            {
                ushort target = bus.ReadImmediate16();
                if (TestCond(ref r, cc)) { bus.InternalCycle(); Push16(ref r, bus, r.Pc); r.Pc = target; return 24; }
                return 12;
            };
        }

        // CALL a16 — 6 M-cycles: fetch + 2 imm + internal + 2 writes.
        t[0xCD] = (ref CpuRegisters r, IInstructionBus bus) => { ushort target = bus.ReadImmediate16(); bus.InternalCycle(); Push16(ref r, bus, r.Pc); r.Pc = target; return 24; };

        // RST xx — 4 M-cycles: fetch + internal + 2 writes.
        for (int i = 0; i < 8; i++)
        {
            ushort vec = (ushort)(i * 8);
            int opcode = 0xC7 + i * 8;
            t[opcode] = (ref CpuRegisters r, IInstructionBus bus) => { bus.InternalCycle(); Push16(ref r, bus, r.Pc); r.Pc = vec; return 16; };
        }

        // RET — 4 M-cycles: fetch + 2 reads + internal (PC load).
        t[0xC9] = (ref CpuRegisters r, IInstructionBus bus) => { r.Pc = Pop16(ref r, bus); bus.InternalCycle(); return 16; };
        // RETI — same shape as RET.
        t[0xD9] = (ref CpuRegisters r, IInstructionBus bus) => { r.Pc = Pop16(ref r, bus); bus.InternalCycle(); bus.SetIme(true); return 16; };

        // ALU A,d8
        t[0xC6] = (ref CpuRegisters r, IInstructionBus bus) => { AluAdd(ref r, bus.ReadImmediate(), false); return 8; };
        t[0xCE] = (ref CpuRegisters r, IInstructionBus bus) => { AluAdd(ref r, bus.ReadImmediate(), true); return 8; };
        t[0xD6] = (ref CpuRegisters r, IInstructionBus bus) => { AluSub(ref r, bus.ReadImmediate(), false, true); return 8; };
        t[0xDE] = (ref CpuRegisters r, IInstructionBus bus) => { AluSub(ref r, bus.ReadImmediate(), true, true); return 8; };
        t[0xE6] = (ref CpuRegisters r, IInstructionBus bus) => { AluAnd(ref r, bus.ReadImmediate()); return 8; };
        t[0xEE] = (ref CpuRegisters r, IInstructionBus bus) => { AluXor(ref r, bus.ReadImmediate()); return 8; };
        t[0xF6] = (ref CpuRegisters r, IInstructionBus bus) => { AluOr(ref r, bus.ReadImmediate()); return 8; };
        t[0xFE] = (ref CpuRegisters r, IInstructionBus bus) => { AluSub(ref r, bus.ReadImmediate(), false, false); return 8; };

        // LDH ($FF00+n),A / LDH A,($FF00+n)
        t[0xE0] = (ref CpuRegisters r, IInstructionBus bus) => { bus.WriteByte((ushort)(0xFF00 | bus.ReadImmediate()), r.A); return 12; };
        t[0xF0] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = bus.ReadByte((ushort)(0xFF00 | bus.ReadImmediate())); return 12; };

        // LD ($FF00+C),A / LD A,($FF00+C)
        t[0xE2] = (ref CpuRegisters r, IInstructionBus bus) => { bus.WriteByte((ushort)(0xFF00 | r.C), r.A); return 8; };
        t[0xF2] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = bus.ReadByte((ushort)(0xFF00 | r.C)); return 8; };

        // ADD SP,r8 — 4 M-cycles: fetch + imm + 2 internal.
        t[0xE8] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            sbyte offset = (sbyte)bus.ReadImmediate();
            ushort sp = r.Sp;
            int result = sp + offset;
            r.SetFlag(CpuRegisters.FlagZ, false);
            r.SetFlag(CpuRegisters.FlagN, false);
            r.SetFlag(CpuRegisters.FlagH, ((sp & 0x0F) + (offset & 0x0F)) > 0x0F);
            r.SetFlag(CpuRegisters.FlagC, ((sp & 0xFF) + (offset & 0xFF)) > 0xFF);
            r.Sp = (ushort)result;
            bus.InternalCycle();
            bus.InternalCycle();
            return 16;
        };

        // JP (HL)
        t[0xE9] = (ref CpuRegisters r, IInstructionBus bus) => { r.Pc = r.HL; return 4; };

        // LD (a16),A / LD A,(a16)
        t[0xEA] = (ref CpuRegisters r, IInstructionBus bus) => { bus.WriteByte(bus.ReadImmediate16(), r.A); return 16; };
        t[0xFA] = (ref CpuRegisters r, IInstructionBus bus) => { r.A = bus.ReadByte(bus.ReadImmediate16()); return 16; };

        // LD HL,SP+r8 — 3 M-cycles: fetch + imm + 1 internal.
        t[0xF8] = (ref CpuRegisters r, IInstructionBus bus) =>
        {
            sbyte offset = (sbyte)bus.ReadImmediate();
            ushort sp = r.Sp;
            int result = sp + offset;
            r.SetFlag(CpuRegisters.FlagZ, false);
            r.SetFlag(CpuRegisters.FlagN, false);
            r.SetFlag(CpuRegisters.FlagH, ((sp & 0x0F) + (offset & 0x0F)) > 0x0F);
            r.SetFlag(CpuRegisters.FlagC, ((sp & 0xFF) + (offset & 0xFF)) > 0xFF);
            r.HL = (ushort)result;
            bus.InternalCycle();
            return 12;
        };

        // LD SP,HL — 2 M-cycles: fetch + 1 internal.
        t[0xF9] = (ref CpuRegisters r, IInstructionBus bus) => { r.Sp = r.HL; bus.InternalCycle(); return 8; };

        // DI / EI
        t[0xF3] = (ref CpuRegisters r, IInstructionBus bus) => { bus.SetIme(false); return 4; };
        t[0xFB] = (ref CpuRegisters r, IInstructionBus bus) => { bus.SetIme(true); return 4; };

        return t;
    }

    // ─────────────────────────────────────────────────────────────
    // CB-prefixed table (rotates, shifts, bit/set/res)
    // ─────────────────────────────────────────────────────────────

    private static InstructionHandler?[] BuildCbPrefixedTable()
    {
        var t = new InstructionHandler?[256];

        for (int op = 0; op < 256; op++)
        {
            int group = op >> 6;
            int bit = (op >> 3) & 7;
            int reg = op & 7;
            int opCap = op;

            t[opCap] = (ref CpuRegisters r, IInstructionBus bus) =>
            {
                byte value = ReadReg8(ref r, bus, reg);
                byte result = value;

                switch (group)
                {
                    case 0:  // rotates and shifts
                        switch (bit)
                        {
                            case 0:  // RLC
                                {
                                    byte carry = (byte)((value >> 7) & 1);
                                    result = (byte)((value << 1) | carry);
                                    r.F = 0;
                                    r.SetFlag(CpuRegisters.FlagC, carry != 0);
                                    r.SetFlag(CpuRegisters.FlagZ, result == 0);
                                }
                                break;
                            case 1:  // RRC
                                {
                                    byte carry = (byte)(value & 1);
                                    result = (byte)((value >> 1) | (carry << 7));
                                    r.F = 0;
                                    r.SetFlag(CpuRegisters.FlagC, carry != 0);
                                    r.SetFlag(CpuRegisters.FlagZ, result == 0);
                                }
                                break;
                            case 2:  // RL
                                {
                                    byte oldC = r.FlagSet(CpuRegisters.FlagC) ? (byte)1 : (byte)0;
                                    byte newC = (byte)((value >> 7) & 1);
                                    result = (byte)((value << 1) | oldC);
                                    r.F = 0;
                                    r.SetFlag(CpuRegisters.FlagC, newC != 0);
                                    r.SetFlag(CpuRegisters.FlagZ, result == 0);
                                }
                                break;
                            case 3:  // RR
                                {
                                    byte oldC = r.FlagSet(CpuRegisters.FlagC) ? (byte)1 : (byte)0;
                                    byte newC = (byte)(value & 1);
                                    result = (byte)((value >> 1) | (oldC << 7));
                                    r.F = 0;
                                    r.SetFlag(CpuRegisters.FlagC, newC != 0);
                                    r.SetFlag(CpuRegisters.FlagZ, result == 0);
                                }
                                break;
                            case 4:  // SLA
                                {
                                    byte carry = (byte)((value >> 7) & 1);
                                    result = (byte)(value << 1);
                                    r.F = 0;
                                    r.SetFlag(CpuRegisters.FlagC, carry != 0);
                                    r.SetFlag(CpuRegisters.FlagZ, result == 0);
                                }
                                break;
                            case 5:  // SRA
                                {
                                    byte carry = (byte)(value & 1);
                                    result = (byte)((value >> 1) | (value & 0x80));
                                    r.F = 0;
                                    r.SetFlag(CpuRegisters.FlagC, carry != 0);
                                    r.SetFlag(CpuRegisters.FlagZ, result == 0);
                                }
                                break;
                            case 6:  // SWAP
                                result = (byte)(((value & 0x0F) << 4) | ((value & 0xF0) >> 4));
                                r.F = 0;
                                r.SetFlag(CpuRegisters.FlagZ, result == 0);
                                break;
                            case 7:  // SRL
                                {
                                    byte carry = (byte)(value & 1);
                                    result = (byte)(value >> 1);
                                    r.F = 0;
                                    r.SetFlag(CpuRegisters.FlagC, carry != 0);
                                    r.SetFlag(CpuRegisters.FlagZ, result == 0);
                                }
                                break;
                        }
                        WriteReg8(ref r, bus, reg, result);
                        break;

                    case 1:  // BIT b,r — does not write back
                        {
                            bool set = (value & (1 << bit)) != 0;
                            r.SetFlag(CpuRegisters.FlagZ, !set);
                            r.SetFlag(CpuRegisters.FlagN, false);
                            r.SetFlag(CpuRegisters.FlagH, true);
                        }
                        return reg == 6 ? 12 : 8;

                    case 2:  // RES b,r
                        result = (byte)(value & ~(1 << bit));
                        WriteReg8(ref r, bus, reg, result);
                        break;

                    case 3:  // SET b,r
                        result = (byte)(value | (1 << bit));
                        WriteReg8(ref r, bus, reg, result);
                        break;
                }

                return reg == 6 ? 16 : 8;
            };
        }

        return t;
    }
}
