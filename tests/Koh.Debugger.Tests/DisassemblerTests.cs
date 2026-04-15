using Koh.Debugger;

namespace Koh.Debugger.Tests;

public class DisassemblerTests
{
    [Test]
    public async Task Decodes_Nop()
    {
        byte[] code = { 0x00 };
        var (mnemonic, length) = Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("NOP");
        await Assert.That(length).IsEqualTo(1);
    }

    [Test]
    public async Task Decodes_Ld_Bc_Imm16()
    {
        byte[] code = { 0x01, 0x34, 0x12 };
        var (mnemonic, length) = Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("LD BC,$1234");
        await Assert.That(length).IsEqualTo(3);
    }

    [Test]
    public async Task Decodes_Jp_A16()
    {
        byte[] code = { 0xC3, 0x00, 0x20 };
        var (mnemonic, length) = Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("JP $2000");
        await Assert.That(length).IsEqualTo(3);
    }

    [Test]
    public async Task Decodes_Ld_R_R()
    {
        // $78 = LD A,B
        byte[] code = { 0x78 };
        var (mnemonic, length) = Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("LD A,B");
        await Assert.That(length).IsEqualTo(1);
    }

    [Test]
    public async Task Decodes_Halt()
    {
        byte[] code = { 0x76 };
        var (mnemonic, length) = Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("HALT");
        await Assert.That(length).IsEqualTo(1);
    }

    [Test]
    public async Task Decodes_Cb_Bit()
    {
        // CB 47 = BIT 0,A
        byte[] code = { 0xCB, 0x47 };
        var (mnemonic, length) = Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("BIT 0,A");
        await Assert.That(length).IsEqualTo(2);
    }

    [Test]
    public async Task Decodes_Rst()
    {
        // $FF = RST $38
        byte[] code = { 0xFF };
        var (mnemonic, length) = Disassembler.DecodeOne(a => code[a], 0);
        await Assert.That(mnemonic).IsEqualTo("RST $38");
        await Assert.That(length).IsEqualTo(1);
    }
}
