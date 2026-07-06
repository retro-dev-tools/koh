using Koh.Core.Encoding;

namespace Koh.Compiler.Tests.Backends;

/// <summary>
/// Pins the raw opcodes the SM83 backend emits to Koh's canonical instruction table, so the two
/// encodings can never silently drift apart. The backend emits bytes directly for speed and CB
/// coverage; this test is the shared-source-of-truth guard.
/// </summary>
public class Sm83EncodingTests
{
    private static byte[] Encoding(string mnemonic, params OperandPattern[] operands)
    {
        var match = Sm83InstructionTable.Lookup(mnemonic)
            .FirstOrDefault(d => d.Operands.AsEnumerable().SequenceEqual(operands));
        if (match is null)
            throw new InvalidOperationException(
                $"no table entry for {mnemonic} {string.Join(",", operands)}");
        return match.Encoding;
    }

    private static byte Op(string mnemonic, params OperandPattern[] operands) =>
        Encoding(mnemonic, operands)[0];

    [Test]
    public async Task Loads_MatchTable()
    {
        await Assert.That(Op("LD", OperandPattern.RegA, OperandPattern.Imm8)).IsEqualTo((byte)0x3E);
        await Assert.That(Op("LD", OperandPattern.RegA, OperandPattern.IndImm16)).IsEqualTo((byte)0xFA);
        await Assert.That(Op("LD", OperandPattern.IndImm16, OperandPattern.RegA)).IsEqualTo((byte)0xEA);
        await Assert.That(Op("LD", OperandPattern.RegB, OperandPattern.RegA)).IsEqualTo((byte)0x47);
        await Assert.That(Op("LD", OperandPattern.RegA, OperandPattern.IndHL)).IsEqualTo((byte)0x7E);
        await Assert.That(Op("LD", OperandPattern.IndHL, OperandPattern.RegA)).IsEqualTo((byte)0x77);
        await Assert.That(Op("LD", OperandPattern.RegE, OperandPattern.RegA)).IsEqualTo((byte)0x5F);
        await Assert.That(Op("LD", OperandPattern.RegD, OperandPattern.RegA)).IsEqualTo((byte)0x57);
        await Assert.That(Op("LD", OperandPattern.RegL, OperandPattern.RegA)).IsEqualTo((byte)0x6F);
        await Assert.That(Op("LD", OperandPattern.RegH, OperandPattern.RegA)).IsEqualTo((byte)0x67);
    }

    [Test]
    public async Task Alu_MatchTable()
    {
        await Assert.That(Op("ADD", OperandPattern.RegA, OperandPattern.RegB)).IsEqualTo((byte)0x80);
        await Assert.That(Op("ADC", OperandPattern.RegA, OperandPattern.RegB)).IsEqualTo((byte)0x88);
        await Assert.That(Op("SUB", OperandPattern.RegB)).IsEqualTo((byte)0x90);
        await Assert.That(Op("SBC", OperandPattern.RegA, OperandPattern.RegB)).IsEqualTo((byte)0x98);
        await Assert.That(Op("AND", OperandPattern.RegB)).IsEqualTo((byte)0xA0);
        await Assert.That(Op("OR", OperandPattern.RegB)).IsEqualTo((byte)0xB0);
        await Assert.That(Op("XOR", OperandPattern.RegB)).IsEqualTo((byte)0xA8);
        await Assert.That(Op("AND", OperandPattern.RegA)).IsEqualTo((byte)0xA7);
        await Assert.That(Op("ADD", OperandPattern.RegA, OperandPattern.Imm8)).IsEqualTo((byte)0xC6);
        await Assert.That(Op("ADD", OperandPattern.RegHL, OperandPattern.RegDE)).IsEqualTo((byte)0x19);
        await Assert.That(Op("INC", OperandPattern.RegHL)).IsEqualTo((byte)0x23);
    }

    [Test]
    public async Task ControlFlow_MatchTable()
    {
        await Assert.That(Op("RET")).IsEqualTo((byte)0xC9);
        await Assert.That(Op("JP", OperandPattern.Imm16)).IsEqualTo((byte)0xC3);
        await Assert.That(Op("JP", OperandPattern.CondNZ, OperandPattern.Imm16)).IsEqualTo((byte)0xC2);
        await Assert.That(Op("JP", OperandPattern.CondZ, OperandPattern.Imm16)).IsEqualTo((byte)0xCA);
        await Assert.That(Op("JP", OperandPattern.CondNC, OperandPattern.Imm16)).IsEqualTo((byte)0xD2);
        await Assert.That(Op("JP", OperandPattern.CondC, OperandPattern.Imm16)).IsEqualTo((byte)0xDA);
        await Assert.That(Op("CALL", OperandPattern.Imm16)).IsEqualTo((byte)0xCD);
    }

    [Test]
    public async Task CbShifts_MatchTable()
    {
        // CB-prefixed: table Encoding is [0xCB, opcode].
        await Assert.That(Encoding("SLA", OperandPattern.RegE)).IsEquivalentTo(new byte[] { 0xCB, 0x23 });
        await Assert.That(Encoding("RL", OperandPattern.RegD)).IsEquivalentTo(new byte[] { 0xCB, 0x12 });
        await Assert.That(Encoding("SRL", OperandPattern.RegE)).IsEquivalentTo(new byte[] { 0xCB, 0x3B });
        await Assert.That(Encoding("SRA", OperandPattern.RegE)).IsEquivalentTo(new byte[] { 0xCB, 0x2B });
        await Assert.That(Encoding("RR", OperandPattern.RegE)).IsEquivalentTo(new byte[] { 0xCB, 0x1B });
    }
}
