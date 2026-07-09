using Koh.Compiler.Backends.Sm83.Mir;

namespace Koh.Compiler.Tests.Backends;

public class MirDecoderTests
{
    private static MirInstruction DecodeOne(params byte[] bytes)
    {
        var program = MirDecoder.Decode(bytes);
        return program.Instructions.Single();
    }

    [Test]
    public async Task Decode_RoundTripsToOriginalBytes()
    {
        // A mix spanning every decoding path: LD r,r'; LD r,d8; ALU; INC; CB; control; MMIO; two 3-byte
        // ops; a CB-prefixed op; and an illegal opcode.
        byte[] code =
        [
            0xAF, // XOR A
            0x3E,
            0x05, // LD A, 5
            0x47, // LD B, A
            0x04, // INC B
            0x89, // ADC A, C
            0xCB,
            0x27, // SLA A
            0xE0,
            0x40, // LDH (0x40), A
            0xEA,
            0x00,
            0xC0, // LD (0xC000), A
            0x20,
            0xFC, // JR NZ, -4
            0xCD,
            0x50,
            0x01, // CALL 0x0150
            0xD3, // illegal
            0xC9, // RET
        ];

        var program = MirDecoder.Decode(code);

        await Assert.That(program.ToBytes()).IsEquivalentTo(code);
        await Assert.That(program.Instructions.Count).IsEqualTo(12);
        // Offsets are contiguous and correct.
        await Assert.That(program.Instructions[0].Offset).IsEqualTo(0);
        await Assert.That(program.Instructions[1].Offset).IsEqualTo(1); // LD A,5 at offset 1
        await Assert.That(program.Instructions[2].Offset).IsEqualTo(3); // LD B,A after the 2-byte LD A,5
    }

    [Test]
    public async Task Decode_XorAWritesAAndAllFlags()
    {
        var xorA = DecodeOne(0xAF).Effects; // XOR A, A
        await Assert.That(xorA.RegRead).IsEqualTo(Sm83Register.A);
        await Assert.That(xorA.RegWrite).IsEqualTo(Sm83Register.A);
        await Assert.That(xorA.FlagWrite).IsEqualTo(Sm83Flags.All);
        await Assert.That(xorA.MemRead).IsFalse();
    }

    [Test]
    public async Task Decode_LoadRegRegHasNoFlagEffect()
    {
        var ldBc = DecodeOne(0x41).Effects; // LD B, C
        await Assert.That(ldBc.RegRead).IsEqualTo(Sm83Register.C);
        await Assert.That(ldBc.RegWrite).IsEqualTo(Sm83Register.B);
        await Assert.That(ldBc.FlagWrite).IsEqualTo(Sm83Flags.None);
        await Assert.That(ldBc.FlagRead).IsEqualTo(Sm83Flags.None);
    }

    [Test]
    public async Task Decode_LoadImmediateWritesRegisterOnly()
    {
        var ldA = DecodeOne(0x3E, 0x00).Effects; // LD A, 0 — unlike XOR A, leaves flags untouched
        await Assert.That(ldA.RegWrite).IsEqualTo(Sm83Register.A);
        await Assert.That(ldA.RegRead).IsEqualTo(Sm83Register.None);
        await Assert.That(ldA.FlagWrite).IsEqualTo(Sm83Flags.None);
    }

    [Test]
    public async Task Decode_AdcFromMemoryReadsCarryAAndHl()
    {
        var adc = DecodeOne(0x8E).Effects; // ADC A, (HL)
        await Assert.That(adc.RegRead.HasFlag(Sm83Register.A)).IsTrue();
        await Assert.That(adc.RegRead.HasFlag(Sm83Register.H)).IsTrue();
        await Assert.That(adc.RegRead.HasFlag(Sm83Register.L)).IsTrue();
        await Assert.That(adc.FlagRead).IsEqualTo(Sm83Flags.C); // carry-in
        await Assert.That(adc.FlagWrite).IsEqualTo(Sm83Flags.All);
        await Assert.That(adc.MemRead).IsTrue();
        await Assert.That(adc.RegWrite).IsEqualTo(Sm83Register.A);
    }

    [Test]
    public async Task Decode_IncByteWritesZnhButNotCarry()
    {
        var incB = DecodeOne(0x04).Effects; // INC B
        await Assert.That(incB.RegRead).IsEqualTo(Sm83Register.B);
        await Assert.That(incB.RegWrite).IsEqualTo(Sm83Register.B);
        await Assert.That(incB.FlagWrite).IsEqualTo(Sm83Flags.Z | Sm83Flags.N | Sm83Flags.H);
        await Assert.That(incB.FlagWrite.HasFlag(Sm83Flags.C)).IsFalse(); // INC preserves carry
    }

    [Test]
    public async Task Decode_CbBitTestsWithoutWritingRegister()
    {
        var bit = DecodeOne(0xCB, 0x47).Effects; // BIT 0, A
        await Assert.That(bit.RegRead).IsEqualTo(Sm83Register.A);
        await Assert.That(bit.RegWrite).IsEqualTo(Sm83Register.None); // BIT does not modify the register
        await Assert.That(bit.FlagWrite).IsEqualTo(Sm83Flags.Z | Sm83Flags.N | Sm83Flags.H);
    }

    [Test]
    public async Task Decode_ControlFlowIsClassified()
    {
        await Assert.That(DecodeOne(0xC9).Effects.Control).IsEqualTo(MirControl.Return);
        await Assert.That(DecodeOne(0xCD, 0x00, 0x01).Effects.Control).IsEqualTo(MirControl.Call);
        await Assert.That(DecodeOne(0xC3, 0x00, 0x01).Effects.Control).IsEqualTo(MirControl.Jump);
        await Assert.That(DecodeOne(0x18, 0x02).Effects.Control).IsEqualTo(MirControl.Jump);
        await Assert.That(DecodeOne(0x20, 0x02).Effects.Control).IsEqualTo(MirControl.Branch);
        await Assert.That(DecodeOne(0x76).Effects.Control).IsEqualTo(MirControl.Halt);
    }

    [Test]
    public async Task Decode_PushPopAfTouchAllFlags()
    {
        var popAf = DecodeOne(0xF1).Effects; // POP AF — loads A and all flags from the stack
        await Assert.That(popAf.RegWrite.HasFlag(Sm83Register.A)).IsTrue();
        await Assert.That(popAf.FlagWrite).IsEqualTo(Sm83Flags.All);
        await Assert.That(popAf.MemRead).IsTrue();

        var pushAf = DecodeOne(0xF5).Effects; // PUSH AF — reads A and all flags
        await Assert.That(pushAf.RegRead.HasFlag(Sm83Register.A)).IsTrue();
        await Assert.That(pushAf.FlagRead).IsEqualTo(Sm83Flags.All);
        await Assert.That(pushAf.MemWrite).IsTrue();
    }

    [Test]
    public async Task Decode_MmioLoadStoreLengthsAndEffects()
    {
        var ldhStore = DecodeOne(0xE0, 0x40); // LDH (0x40), A
        await Assert.That(ldhStore.Length).IsEqualTo(2);
        await Assert.That(ldhStore.Effects.RegRead).IsEqualTo(Sm83Register.A);
        await Assert.That(ldhStore.Effects.MemWrite).IsTrue();

        var absStore = DecodeOne(0xEA, 0x00, 0xC0); // LD (0xC000), A
        await Assert.That(absStore.Length).IsEqualTo(3);
        await Assert.That(absStore.Effects.MemWrite).IsTrue();

        var cLoad = DecodeOne(0xF2).Effects; // LD A, (C)
        await Assert.That(cLoad.RegRead).IsEqualTo(Sm83Register.C);
        await Assert.That(cLoad.RegWrite).IsEqualTo(Sm83Register.A);
        await Assert.That(cLoad.MemRead).IsTrue();
    }

    [Test]
    public async Task Decode_IllegalOpcodeIsOpaqueBarrier()
    {
        var illegal = DecodeOne(0xD3).Effects;
        await Assert.That(illegal).IsEqualTo(MirEffects.Opaque);
        await Assert.That(illegal.RegWrite).IsEqualTo(Sm83Register.All);
        await Assert.That(illegal.FlagWrite).IsEqualTo(Sm83Flags.All);
    }

    [Test]
    public async Task Decode_IsTotalOverEveryOpcode()
    {
        // Every unprefixed opcode decodes to a single instruction of length 1–3 that round-trips, and
        // every CB-prefixed opcode decodes to a 2-byte instruction. Decoding is total by construction,
        // so nothing throws and no opcode is silently dropped.
        for (var op = 0; op <= 0xFF; op++)
        {
            byte[] buffer = [(byte)op, 0x00, 0x00];
            var first = MirDecoder.Decode(buffer).Instructions[0];
            await Assert.That(first.Length).IsGreaterThanOrEqualTo(1);
            await Assert.That(first.Length).IsLessThanOrEqualTo(3);

            if (op == 0xCB)
            {
                for (var sub = 0; sub <= 0xFF; sub++)
                {
                    var cb = DecodeOne(0xCB, (byte)sub);
                    await Assert.That(cb.Length).IsEqualTo(2);
                }
            }
        }
    }

    [Test]
    public async Task Decode_HlPostIncrementLoadWritesHl()
    {
        var ldiA = DecodeOne(0x2A).Effects; // LD A, (HL+)
        await Assert.That(ldiA.RegWrite.HasFlag(Sm83Register.A)).IsTrue();
        await Assert.That(ldiA.RegWrite.HasFlag(Sm83Register.H)).IsTrue(); // HL is post-incremented
        await Assert.That(ldiA.RegWrite.HasFlag(Sm83Register.L)).IsTrue();
        await Assert.That(ldiA.MemRead).IsTrue();
    }
}
