using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class CgbBankingTests
{
    private static GameBoySystem MakeCgbSystem()
    {
        var rom = new byte[0x8000];
        rom[0x143] = 0x80;
        rom[0x147] = 0x00;
        var cart = CartridgeFactory.Load(rom);
        return new GameBoySystem(HardwareMode.Cgb, cart);
    }

    [Test]
    public async Task Vram_Bank_Switch_Isolates_Bytes()
    {
        var gb = MakeCgbSystem();
        gb.Mmu.WriteByte(0xFF4F, 0);
        gb.Mmu.WriteByte(0x8000, 0xAA);
        gb.Mmu.WriteByte(0xFF4F, 1);
        gb.Mmu.WriteByte(0x8000, 0xBB);

        gb.Mmu.WriteByte(0xFF4F, 0);
        byte bank0 = gb.Mmu.ReadByte(0x8000);

        gb.Mmu.WriteByte(0xFF4F, 1);
        byte bank1 = gb.Mmu.ReadByte(0x8000);

        await Assert.That(bank0).IsEqualTo((byte)0xAA);
        await Assert.That(bank1).IsEqualTo((byte)0xBB);
    }

    [Test]
    public async Task Wram_Bank_Switch_Isolates_High_Region()
    {
        var gb = MakeCgbSystem();
        gb.Mmu.WriteByte(0xFF70, 2);
        gb.Mmu.WriteByte(0xD000, 0x11);
        gb.Mmu.WriteByte(0xFF70, 3);
        gb.Mmu.WriteByte(0xD000, 0x22);

        gb.Mmu.WriteByte(0xFF70, 2);
        byte result = gb.Mmu.ReadByte(0xD000);
        await Assert.That(result).IsEqualTo((byte)0x11);
    }

    [Test]
    public async Task Wram_Bank_0_Aliases_To_Bank_1()
    {
        var gb = MakeCgbSystem();
        gb.Mmu.WriteByte(0xFF70, 0);
        gb.Mmu.WriteByte(0xD000, 0x77);
        gb.Mmu.WriteByte(0xFF70, 1);
        byte result = gb.Mmu.ReadByte(0xD000);
        await Assert.That(result).IsEqualTo((byte)0x77);
    }

    [Test]
    public async Task KeyOne_Toggle_Enables_Double_Speed_After_Stop()
    {
        var gb = MakeCgbSystem();
        gb.Mmu.WriteByte(0xFF4D, 0x01);  // arm switch
        bool armedBefore = (gb.Mmu.ReadByte(0xFF4D) & 0x01) != 0;

        gb.KeyOne.OnStopExecuted();
        bool doubleSpeed = (gb.Mmu.ReadByte(0xFF4D) & 0x80) != 0;
        bool armedAfter = (gb.Mmu.ReadByte(0xFF4D) & 0x01) != 0;

        await Assert.That(armedBefore).IsTrue();
        await Assert.That(doubleSpeed).IsTrue();
        await Assert.That(armedAfter).IsFalse();
    }
}
