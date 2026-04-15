using Koh.Emulator.Core.Bus;
using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class MmuTests
{
    private static Mmu MakeMmu()
    {
        var rom = new byte[0x8000];
        rom[0x0000] = 0xAA;
        rom[0x7FFF] = 0xBB;
        rom[0x143] = 0x00;
        rom[0x147] = 0x00;
        rom[0x148] = 0x01;
        var cart = CartridgeFactory.Load(rom);
        var timer = new Timer.Timer();
        var io = new IoRegisters(timer);
        return new Mmu(cart, io);
    }

    [Test]
    public async Task Read_Bank0_Rom()
    {
        var mmu = MakeMmu();
        await Assert.That(mmu.ReadByte(0x0000)).IsEqualTo((byte)0xAA);
    }

    [Test]
    public async Task Read_Wram_After_Write()
    {
        var mmu = MakeMmu();
        mmu.WriteByte(0xC123, 0x77);
        await Assert.That(mmu.ReadByte(0xC123)).IsEqualTo((byte)0x77);
    }

    [Test]
    public async Task EchoRam_Mirrors_Wram()
    {
        var mmu = MakeMmu();
        mmu.WriteByte(0xC234, 0x88);
        await Assert.That(mmu.ReadByte(0xE234)).IsEqualTo((byte)0x88);
    }

    [Test]
    public async Task Prohibited_Region_Reads_Zero()
    {
        var mmu = MakeMmu();
        await Assert.That(mmu.ReadByte(0xFEA5)).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Hram_RoundTrip()
    {
        var mmu = MakeMmu();
        mmu.WriteByte(0xFF90, 0x55);
        await Assert.That(mmu.ReadByte(0xFF90)).IsEqualTo((byte)0x55);
    }

    [Test]
    public async Task IE_Register_Is_Fully_8Bit_Readable_Writable()
    {
        // Per pandocs, IE at $FFFF is a plain 8-bit R/W register. Only bits
        // 0..4 affect interrupt dispatch, but bits 5..7 read back as written.
        var mmu = MakeMmu();
        mmu.WriteByte(0xFFFF, 0xFF);
        await Assert.That(mmu.ReadByte(0xFFFF)).IsEqualTo((byte)0xFF);
    }
}
