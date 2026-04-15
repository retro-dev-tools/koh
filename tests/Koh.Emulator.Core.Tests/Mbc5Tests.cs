using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class Mbc5Tests
{
    private static Koh.Emulator.Core.Cartridge.Cartridge MakeCart(int romBanks = 8)
    {
        var rom = new byte[romBanks * 0x4000];
        rom[0x147] = 0x1B;   // MBC5 + RAM + battery
        rom[0x148] = romBanks switch { 2 => 0x00, 4 => 0x01, 8 => 0x02, 16 => 0x03, 32 => 0x04, _ => 0x02 };
        rom[0x149] = 0x03;   // 32 KiB RAM (4 banks)
        rom[0x0000] = 0xB0;
        rom[0x4000] = 0xB1;
        rom[0x8000] = 0xB2;
        rom[0xC000] = 0xB3;
        return CartridgeFactory.Load(rom);
    }

    [Test]
    public async Task Bank_Low_Select_Selects_Rom_Bank()
    {
        var cart = MakeCart();
        cart.WriteRom(0x2000, 0x02);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)0xB2);
        cart.WriteRom(0x2000, 0x03);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)0xB3);
    }

    [Test]
    public async Task Bank_Zero_Is_Selectable_On_Mbc5_Unlike_Mbc1()
    {
        var cart = MakeCart();
        cart.WriteRom(0x2000, 0x00);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)0xB0);
    }

    [Test]
    public async Task Ram_Bank_Select_Switches_Ram_Bank()
    {
        var cart = MakeCart();
        cart.WriteRom(0x0000, 0x0A);            // enable
        cart.WriteRom(0x4000, 0x00);
        cart.WriteRam(0xA000, 0x11);
        cart.WriteRom(0x4000, 0x01);
        cart.WriteRam(0xA000, 0x22);
        cart.WriteRom(0x4000, 0x00);
        await Assert.That(cart.ReadRam(0xA000)).IsEqualTo((byte)0x11);
        cart.WriteRom(0x4000, 0x01);
        await Assert.That(cart.ReadRam(0xA000)).IsEqualTo((byte)0x22);
    }
}
