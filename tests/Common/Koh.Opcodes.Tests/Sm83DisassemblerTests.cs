using Koh.Opcodes;

namespace Koh.Opcodes.Tests;

public class Sm83DisassemblerTests
{
    private static readonly byte[] IllegalOpcodes =
    [
        0xD3,
        0xDB,
        0xDD,
        0xE3,
        0xE4,
        0xEB,
        0xEC,
        0xED,
        0xF4,
        0xFC,
        0xFD,
    ];

    [Test]
    public async Task Decodes_Nop()
    {
        byte[] code = { 0x00 };
        var (mnemonic, length) = Sm83Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("NOP");
        await Assert.That(length).IsEqualTo(1);
    }

    [Test]
    public async Task Decodes_Ld_Bc_Imm16()
    {
        byte[] code = { 0x01, 0x34, 0x12 };
        var (mnemonic, length) = Sm83Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("LD BC,$1234");
        await Assert.That(length).IsEqualTo(3);
    }

    [Test]
    public async Task Decodes_Jp_A16()
    {
        byte[] code = { 0xC3, 0x00, 0x20 };
        var (mnemonic, length) = Sm83Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("JP $2000");
        await Assert.That(length).IsEqualTo(3);
    }

    [Test]
    public async Task Decodes_Ld_R_R()
    {
        // $78 = LD A,B
        byte[] code = { 0x78 };
        var (mnemonic, length) = Sm83Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("LD A,B");
        await Assert.That(length).IsEqualTo(1);
    }

    [Test]
    public async Task Decodes_Halt()
    {
        byte[] code = { 0x76 };
        var (mnemonic, length) = Sm83Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("HALT");
        await Assert.That(length).IsEqualTo(1);
    }

    [Test]
    public async Task Decodes_Cb_Bit()
    {
        // CB 47 = BIT 0,A
        byte[] code = { 0xCB, 0x47 };
        var (mnemonic, length) = Sm83Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("BIT 0,A");
        await Assert.That(length).IsEqualTo(2);
    }

    [Test]
    public async Task Decodes_Rst()
    {
        // $FF = RST $38
        byte[] code = { 0xFF };
        var (mnemonic, length) = Sm83Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("RST $38");
        await Assert.That(length).IsEqualTo(1);
    }

    // The 0xD_ conditional control-flow opcodes (carry-flag variants) — the SM83 backend emits absolute
    // JP C/JP NC for loop back edges, so a disassembler that skips them desyncs mid-function.
    [Test]
    [Arguments(new byte[] { 0xD2, 0x00, 0x40 }, "JP NC,$4000", 3)]
    [Arguments(new byte[] { 0xD4, 0x00, 0x40 }, "CALL NC,$4000", 3)]
    [Arguments(new byte[] { 0xD8 }, "RET C", 1)]
    [Arguments(new byte[] { 0xDA, 0x51, 0x4E }, "JP C,$4E51", 3)]
    [Arguments(new byte[] { 0xDC, 0x00, 0x40 }, "CALL C,$4000", 3)]
    public async Task Decodes_CarryConditionalOps(byte[] code, string expected, int expectedLength)
    {
        var (mnemonic, length) = Sm83Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo(expected);
        await Assert.That(length).IsEqualTo(expectedLength);
    }

    // Unknown to the debugger's previous hand-written decoder, which printed "??" with length 1.
    [Test]
    [Arguments(new byte[] { 0x16, 0x34 }, "LD D,$34", 2)]
    [Arguments(new byte[] { 0x36, 0x34 }, "LD (HL),$34", 2)]
    [Arguments(new byte[] { 0x34 }, "INC (HL)", 1)]
    [Arguments(new byte[] { 0x39 }, "ADD HL,SP", 1)]
    [Arguments(new byte[] { 0x1B }, "DEC DE", 1)]
    public async Task Decodes_OpcodesThePreviousDecoderMissed(
        byte[] code,
        string expected,
        int expectedLength
    )
    {
        var (mnemonic, length) = Sm83Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo(expected);
        await Assert.That(length).IsEqualTo(expectedLength);
    }

    // Opcodes with several table entries render the canonical (first) one.
    [Test]
    [Arguments(new byte[] { 0x2A }, "LD A,(HL+)")]
    [Arguments(new byte[] { 0x90 }, "SUB A,B")]
    [Arguments(new byte[] { 0xF2 }, "LDH A,($FF00+C)")]
    public async Task Decodes_AliasesAsCanonicalForm(byte[] code, string expected)
    {
        var (mnemonic, _) = Sm83Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo(expected);
    }

    [Test]
    public async Task Decodes_EveryLegalOpcode_AndMarksIllegalOnes()
    {
        var unknown = new List<string>();
        for (int op = 0; op < 256; op++)
        {
            byte[] plain = { (byte)op, 0x34, 0x12 };
            byte[] cb = { 0xCB, (byte)op };
            var (plainText, plainLength) = Sm83Disassembler.DecodeOne(a => plain[a], 0);
            var (cbText, _) = Sm83Disassembler.DecodeOne(a => cb[a], 0);

            bool illegal = IllegalOpcodes.Contains((byte)op);
            if (plainText.StartsWith("??") != illegal || (illegal && plainLength != 1))
                unknown.Add($"${op:X2} -> {plainText} ({plainLength})");
            if (cbText.StartsWith("??"))
                unknown.Add($"CB ${op:X2} -> {cbText}");
        }
        await Assert.That(unknown).IsEmpty();
    }
}
