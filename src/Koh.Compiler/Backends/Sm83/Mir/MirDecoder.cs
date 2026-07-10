namespace Koh.Compiler.Backends.Sm83.Mir;

/// <summary>
/// Lifts a flat SM83 byte region into typed <see cref="MirInstruction"/>s with a full
/// <see cref="MirEffects"/> footprint per instruction, computed structurally from the opcode. The SM83
/// encoding is highly regular — the <c>LD r,r'</c> block (0x40–0x7F), the ALU block (0x80–0xBF), and
/// all CB-prefixed instructions decompose from bit fields — so most effects fall out of arithmetic on
/// the opcode; the less regular 0x00–0x3F and 0xC0–0xFF rows are handled explicitly. Any encoding the
/// decoder does not model (an illegal opcode) is lifted with <see cref="MirEffects.Opaque"/> and its
/// table length, so decoding is total and always re-encodes losslessly.
///
/// This replaces, in a reusable and semantics-aware form, the ad-hoc opcode-length table the current
/// byte-buffer peephole carries: a consumer gets instruction boundaries <em>and</em> the read/write
/// footprint it needs to reason about flag liveness or instruction equivalence.
/// </summary>
public static class MirDecoder
{
    public static MirProgram Decode(ReadOnlyMemory<byte> code)
    {
        var span = code.Span;
        // Instruction count is bounded by the byte count (every instruction is ≥ 1 byte), so sizing to
        // that upper bound decodes without any List regrowth. Each MirInstruction is a slice of `code`,
        // not a copy, so nothing is allocated per instruction.
        var instructions = new List<MirInstruction>(span.Length);
        var offset = 0;
        while (offset < span.Length)
        {
            var naturalLength = InstructionLength(span, offset);
            var length = Math.Min(naturalLength, span.Length - offset);
            // A truncated tail (e.g. a region ending on a lone 0xCB or mid-operand) has no complete
            // instruction to reason about — and computing its effects would read past the buffer, since
            // EffectsOf dereferences the CB sub-opcode. Keep the bytes so decoding still round-trips, but
            // give them an opaque footprint so no consumer treats a partial instruction as analyzable.
            var truncated = length < naturalLength;
            var effects = truncated ? MirEffects.Opaque : EffectsOf(span, offset);
            instructions.Add(new MirInstruction(code, offset, length, effects));
            offset += length;
        }
        return new MirProgram(instructions);
    }

    /// <summary>Encoded length of the instruction at <paramref name="offset"/> (CB-prefixed = 2).
    /// The public length lookup lives in the shared <see cref="Sm83OpcodeLength"/> table (the single
    /// source shared with the byte peephole); this is just Decode's internal boundary helper.</summary>
    private static int InstructionLength(ReadOnlySpan<byte> code, int offset) =>
        Sm83OpcodeLength.Of(code[offset]);

    // ---- Effect computation --------------------------------------------------

    private static MirEffects EffectsOf(ReadOnlySpan<byte> code, int offset)
    {
        var op = code[offset];
        return op switch
        {
            0xCB => CbEffects(code[offset + 1]),
            >= 0x40 and <= 0x7F => op == 0x76 ? Halt() : LoadRegReg(op),
            >= 0x80 and <= 0xBF => AluReg((op >> 3) & 7, op & 7),
            _ => MiscEffects(op),
        };
    }

    private static MirEffects Halt() =>
        new(default, default, default, default, false, false, MirControl.Halt);

    /// <summary>0x40–0x7F: <c>LD dst, src</c> where each operand is an 8-bit register or <c>(HL)</c>.</summary>
    private static MirEffects LoadRegReg(byte op)
    {
        var (srcReg, srcMem) = Reg8((op) & 7);
        var (dstReg, dstMem) = Reg8((op >> 3) & 7);
        var read = srcReg | (srcMem ? Sm83Register.Hl : 0) | (dstMem ? Sm83Register.Hl : 0);
        return new MirEffects(
            read,
            dstMem ? Sm83Register.None : dstReg,
            Sm83Flags.None,
            Sm83Flags.None,
            srcMem,
            dstMem,
            MirControl.Fallthrough
        );
    }

    /// <summary>0x80–0xBF and the immediate ALU ops: <c>ADD/ADC/SUB/SBC/AND/XOR/OR/CP A, operand</c>.
    /// All write every flag; ADC/SBC also read carry; CP writes flags only.</summary>
    private static MirEffects AluReg(int group, int src)
    {
        var (srcReg, srcMem) = Reg8(src);
        return Alu(group, srcReg | (srcMem ? Sm83Register.Hl : 0), srcMem);
    }

    private static MirEffects AluImm(int group) => Alu(group, Sm83Register.None, false);

    private static MirEffects Alu(int group, Sm83Register srcRead, bool memRead)
    {
        var flagRead = group is 1 or 3 ? Sm83Flags.C : Sm83Flags.None; // ADC / SBC read carry
        var writesA = group != 7; // CP (group 7) does not write A
        return new MirEffects(
            Sm83Register.A | srcRead,
            writesA ? Sm83Register.A : Sm83Register.None,
            flagRead,
            Sm83Flags.All,
            memRead,
            false,
            MirControl.Fallthrough
        );
    }

    /// <summary>CB-prefixed: rotates/shifts (write all flags), <c>BIT</c> (writes Z/N/H), <c>RES</c>/
    /// <c>SET</c> (no flags). The operand register is <c>sub &amp; 7</c>.</summary>
    private static MirEffects CbEffects(byte sub)
    {
        var (reg, mem) = Reg8(sub & 7);
        var addr = mem ? Sm83Register.Hl : Sm83Register.None;
        switch (sub >> 6)
        {
            case 0: // rotate / shift
                var flagRead = (sub >> 3 & 7) is 2 or 3 ? Sm83Flags.C : Sm83Flags.None; // RL / RR
                return new MirEffects(
                    reg | addr,
                    mem ? Sm83Register.None : reg,
                    flagRead,
                    Sm83Flags.All,
                    mem,
                    mem,
                    MirControl.Fallthrough
                );
            case 1: // BIT b, r — tests a bit, writes Z (N reset, H set), never writes the register
                return new MirEffects(
                    reg | addr,
                    Sm83Register.None,
                    Sm83Flags.None,
                    Sm83Flags.Z | Sm83Flags.N | Sm83Flags.H,
                    mem,
                    false,
                    MirControl.Fallthrough
                );
            default: // RES / SET — clear or set a bit, no flags
                return new MirEffects(
                    reg | addr,
                    mem ? Sm83Register.None : reg,
                    Sm83Flags.None,
                    Sm83Flags.None,
                    mem,
                    mem,
                    MirControl.Fallthrough
                );
        }
    }

    /// <summary>0x00–0x3F and 0xC0–0xFF — the less regular rows, handled per opcode family.</summary>
    private static MirEffects MiscEffects(byte op)
    {
        // The 0x00–0x3F block is column-structured; 0xC0–0xFF (which shares low nibbles) is not, so the
        // column decoding below is gated to op < 0x40 and everything else falls to the opcode switch.
        if (op < 0x40)
        {
            switch (op & 0x0F)
            {
                case 0x01: // LD r16, d16
                    return WriteReg(Reg16(op));
                case 0x03: // INC r16
                case 0x0B: // DEC r16
                    return ReadWriteReg(Reg16(op));
                case 0x02: // LD (r16), A  /  LD (HL±), A
                case 0x0A: // LD A, (r16)  /  LD A, (HL±)
                    return LoadIndirectAccumulator(op);
                case 0x09: // ADD HL, r16 — writes N/H/C (not Z)
                    return new MirEffects(
                        Sm83Register.Hl | Reg16(op),
                        Sm83Register.Hl,
                        Sm83Flags.None,
                        Sm83Flags.N | Sm83Flags.H | Sm83Flags.C,
                        false,
                        false,
                        MirControl.Fallthrough
                    );
            }
            // INC/DEC/LD r8 recur at both low-nibble halves, so key on the 3-bit slot: 4=INC, 5=DEC,
            // 6=LD r8,d8, with the register in bits 3–5.
            switch (op & 7)
            {
                case 4: // INC r8
                case 5: // DEC r8 — both write Z/N/H, not C
                    var (r, mem) = Reg8((op >> 3) & 7);
                    return new MirEffects(
                        r | (mem ? Sm83Register.Hl : 0),
                        mem ? Sm83Register.None : r,
                        Sm83Flags.None,
                        Sm83Flags.Z | Sm83Flags.N | Sm83Flags.H,
                        mem,
                        mem,
                        MirControl.Fallthrough
                    );
                case 6: // LD r8, d8
                    var (dr, dmem) = Reg8((op >> 3) & 7);
                    return new MirEffects(
                        dmem ? Sm83Register.Hl : 0,
                        dmem ? Sm83Register.None : dr,
                        Sm83Flags.None,
                        Sm83Flags.None,
                        false,
                        dmem,
                        MirControl.Fallthrough
                    );
            }
        }

        return op switch
        {
            0x00 => Nothing(), // NOP — genuinely inert
            0xF3 or 0xFB => InterruptToggle(), // DI / EI — toggles IME (a side effect, not removable)
            0x10 => new MirEffects(
                default,
                default,
                default,
                default,
                false,
                false,
                MirControl.Halt
            ), // STOP
            0x08 => new MirEffects(
                Sm83Register.Sp,
                default,
                default,
                default,
                false,
                true,
                MirControl.Fallthrough
            ), // LD (a16),SP
            0x07 or 0x0F => AccumulatorRotate(readsCarry: false), // RLCA / RRCA
            0x17 or 0x1F => AccumulatorRotate(readsCarry: true), // RLA / RRA
            0x27 => new MirEffects(
                Sm83Register.A,
                Sm83Register.A,
                Sm83Flags.N | Sm83Flags.H | Sm83Flags.C,
                Sm83Flags.Z | Sm83Flags.H | Sm83Flags.C,
                false,
                false,
                MirControl.Fallthrough
            ), // DAA
            0x2F => new MirEffects(
                Sm83Register.A,
                Sm83Register.A,
                default,
                Sm83Flags.N | Sm83Flags.H,
                false,
                false,
                MirControl.Fallthrough
            ), // CPL
            0x37 => new MirEffects(
                default,
                default,
                default,
                Sm83Flags.N | Sm83Flags.H | Sm83Flags.C,
                false,
                false,
                MirControl.Fallthrough
            ), // SCF
            0x3F => new MirEffects(
                default,
                default,
                Sm83Flags.C,
                Sm83Flags.N | Sm83Flags.H | Sm83Flags.C,
                false,
                false,
                MirControl.Fallthrough
            ), // CCF
            0x18 => Control(MirControl.Jump), // JR r8
            0x20 or 0x28 => new MirEffects(
                default,
                default,
                Sm83Flags.Z,
                default,
                false,
                false,
                MirControl.Branch
            ), // JR NZ/Z
            0x30 or 0x38 => new MirEffects(
                default,
                default,
                Sm83Flags.C,
                default,
                false,
                false,
                MirControl.Branch
            ), // JR NC/C
            0xC3 => Control(MirControl.Jump), // JP a16
            0xE9 => new MirEffects(
                Sm83Register.Hl,
                default,
                default,
                default,
                false,
                false,
                MirControl.Jump
            ), // JP HL
            0xC2 or 0xCA => new MirEffects(
                default,
                default,
                Sm83Flags.Z,
                default,
                false,
                false,
                MirControl.Branch
            ), // JP NZ/Z
            0xD2 or 0xDA => new MirEffects(
                default,
                default,
                Sm83Flags.C,
                default,
                false,
                false,
                MirControl.Branch
            ), // JP NC/C
            0xCD => Stack(MirControl.Call), // CALL a16
            0xC4 or 0xCC => CondStack(Sm83Flags.Z, MirControl.Call), // CALL NZ/Z
            0xD4 or 0xDC => CondStack(Sm83Flags.C, MirControl.Call), // CALL NC/C
            0xC7 or 0xCF or 0xD7 or 0xDF or 0xE7 or 0xEF or 0xF7 or 0xFF => Stack(MirControl.Call), // RST
            0xC9 => new MirEffects(
                Sm83Register.Sp,
                Sm83Register.Sp,
                default,
                default,
                true,
                false,
                MirControl.Return
            ), // RET
            0xD9 => new MirEffects(
                Sm83Register.Sp,
                Sm83Register.Sp,
                default,
                default,
                true,
                false,
                MirControl.Return,
                SideEffect: true
            ), // RETI — also re-enables interrupts (IME)
            0xC0 or 0xC8 => new MirEffects(
                Sm83Register.Sp,
                Sm83Register.Sp,
                Sm83Flags.Z,
                default,
                true,
                false,
                MirControl.Return
            ), // RET NZ/Z
            0xD0 or 0xD8 => new MirEffects(
                Sm83Register.Sp,
                Sm83Register.Sp,
                Sm83Flags.C,
                default,
                true,
                false,
                MirControl.Return
            ), // RET NC/C
            0xC1 or 0xD1 or 0xE1 => Pop(Reg16Stack(op), default), // POP BC/DE/HL
            0xF1 => Pop(Sm83Register.A, Sm83Flags.All), // POP AF — restores A and all flags
            0xC5 or 0xD5 or 0xE5 => Push(Reg16Stack(op), default), // PUSH BC/DE/HL
            0xF5 => Push(Sm83Register.A, Sm83Flags.All), // PUSH AF
            0xC6 or 0xCE or 0xD6 or 0xDE or 0xE6 or 0xEE or 0xF6 or 0xFE => AluImm((op >> 3) & 7), // ALU A,d8
            0xE0 => new MirEffects(
                Sm83Register.A,
                default,
                default,
                default,
                false,
                true,
                MirControl.Fallthrough
            ), // LDH (a8),A
            0xF0 => new MirEffects(
                default,
                Sm83Register.A,
                default,
                default,
                true,
                false,
                MirControl.Fallthrough
            ), // LDH A,(a8)
            0xE2 => new MirEffects(
                Sm83Register.A | Sm83Register.C,
                default,
                default,
                default,
                false,
                true,
                MirControl.Fallthrough
            ), // LD (C),A
            0xF2 => new MirEffects(
                Sm83Register.C,
                Sm83Register.A,
                default,
                default,
                true,
                false,
                MirControl.Fallthrough
            ), // LD A,(C)
            0xEA => new MirEffects(
                Sm83Register.A,
                default,
                default,
                default,
                false,
                true,
                MirControl.Fallthrough
            ), // LD (a16),A
            0xFA => new MirEffects(
                default,
                Sm83Register.A,
                default,
                default,
                true,
                false,
                MirControl.Fallthrough
            ), // LD A,(a16)
            0xE8 => new MirEffects(
                Sm83Register.Sp,
                Sm83Register.Sp,
                default,
                Sm83Flags.All,
                false,
                false,
                MirControl.Fallthrough
            ), // ADD SP,r8
            0xF8 => new MirEffects(
                Sm83Register.Sp,
                Sm83Register.Hl,
                default,
                Sm83Flags.All,
                false,
                false,
                MirControl.Fallthrough
            ), // LD HL,SP+r8
            0xF9 => new MirEffects(
                Sm83Register.Hl,
                Sm83Register.Sp,
                default,
                default,
                false,
                false,
                MirControl.Fallthrough
            ), // LD SP,HL
            _ => MirEffects.Opaque, // illegal / unmodeled opcode — treat as a barrier
        };
    }

    // ---- Small effect builders ----------------------------------------------

    private static MirEffects Nothing() =>
        new(default, default, default, default, false, false, MirControl.Fallthrough);

    /// <summary><c>DI</c>/<c>EI</c>: no register/flag/memory footprint, but toggling the interrupt-master
    /// flag is an observable side effect, so the instruction is marked non-removable and non-reorderable.</summary>
    private static MirEffects InterruptToggle() =>
        new(
            default,
            default,
            default,
            default,
            false,
            false,
            MirControl.Fallthrough,
            SideEffect: true
        );

    private static MirEffects Control(MirControl control) =>
        new(default, default, default, default, false, false, control);

    private static MirEffects Stack(MirControl control) =>
        new(Sm83Register.Sp, Sm83Register.Sp, default, default, false, true, control);

    private static MirEffects CondStack(Sm83Flags read, MirControl control) =>
        new(Sm83Register.Sp, Sm83Register.Sp, read, default, false, true, control);

    private static MirEffects Push(Sm83Register reg, Sm83Flags flags) =>
        new(
            Sm83Register.Sp | reg,
            Sm83Register.Sp,
            flags,
            default,
            false,
            true,
            MirControl.Fallthrough
        );

    private static MirEffects Pop(Sm83Register reg, Sm83Flags flags) =>
        new(
            Sm83Register.Sp,
            Sm83Register.Sp | reg,
            default,
            flags,
            true,
            false,
            MirControl.Fallthrough
        );

    private static MirEffects WriteReg(Sm83Register reg) =>
        new(default, reg, default, default, false, false, MirControl.Fallthrough);

    private static MirEffects ReadWriteReg(Sm83Register reg) =>
        new(reg, reg, default, default, false, false, MirControl.Fallthrough);

    private static MirEffects AccumulatorRotate(bool readsCarry) =>
        new(
            Sm83Register.A,
            Sm83Register.A,
            readsCarry ? Sm83Flags.C : Sm83Flags.None,
            Sm83Flags.All,
            false,
            false,
            MirControl.Fallthrough
        );

    /// <summary>0x02/0x12/0x22/0x32 (<c>LD (r16),A</c>) and 0x0A/0x1A/0x2A/0x3A (<c>LD A,(r16)</c>),
    /// including the <c>HL+</c>/<c>HL-</c> post-increment forms which also write HL.</summary>
    private static MirEffects LoadIndirectAccumulator(byte op)
    {
        var high = (op >> 4) & 3;
        var addr = high switch
        {
            0 => Sm83Register.Bc,
            1 => Sm83Register.De,
            _ => Sm83Register.Hl, // 0x2x/0x3x use HL with post-increment/decrement
        };
        var writesHl = high >= 2 ? Sm83Register.Hl : Sm83Register.None; // HL+/HL- mutate HL
        var toMemory = (op & 0x0F) == 0x02; // 0x_2 stores A, 0x_A loads A
        return toMemory
            ? new MirEffects(
                Sm83Register.A | addr,
                writesHl,
                default,
                default,
                false,
                true,
                MirControl.Fallthrough
            )
            : new MirEffects(
                addr,
                Sm83Register.A | writesHl,
                default,
                default,
                true,
                false,
                MirControl.Fallthrough
            );
    }

    // ---- Operand decoding ----------------------------------------------------

    /// <summary>The 8-bit operand slot: index 0–7 → B,C,D,E,H,L,(HL),A. Index 6 is the <c>(HL)</c>
    /// memory operand, reported as register <c>None</c> with the memory flag set by the caller.</summary>
    private static (Sm83Register Reg, bool Mem) Reg8(int index) =>
        index switch
        {
            0 => (Sm83Register.B, false),
            1 => (Sm83Register.C, false),
            2 => (Sm83Register.D, false),
            3 => (Sm83Register.E, false),
            4 => (Sm83Register.H, false),
            5 => (Sm83Register.L, false),
            6 => (Sm83Register.None, true), // (HL)
            _ => (Sm83Register.A, false),
        };

    /// <summary>The 16-bit operand column for 0x_1/0x_3/0x_9/0x_B: BC, DE, HL, SP.</summary>
    private static Sm83Register Reg16(byte op) =>
        ((op >> 4) & 3) switch
        {
            0 => Sm83Register.Bc,
            1 => Sm83Register.De,
            2 => Sm83Register.Hl,
            _ => Sm83Register.Sp,
        };

    /// <summary>The 16-bit operand for PUSH/POP: BC, DE, HL (AF is handled by the caller).</summary>
    private static Sm83Register Reg16Stack(byte op) =>
        ((op >> 4) & 3) switch
        {
            0 => Sm83Register.Bc,
            1 => Sm83Register.De,
            _ => Sm83Register.Hl,
        };
}
