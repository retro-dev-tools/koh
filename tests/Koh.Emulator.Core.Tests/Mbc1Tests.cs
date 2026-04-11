using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class Mbc1Tests
{
    private static Cartridge.Cartridge MakeMbc1(int romBanks, int ramBanks)
    {
        int romSizeCode = romBanks switch
        {
            2 => 0x00,
            4 => 0x01,
            8 => 0x02,
            16 => 0x03,
            32 => 0x04,
            64 => 0x05,
            128 => 0x06,
            _ => 0x00,
        };
        int ramSizeCode = ramBanks switch
        {
            0 => 0x00,
            1 => 0x02,
            4 => 0x03,
            _ => 0x00,
        };

        var rom = new byte[romBanks * 0x4000];
        rom[0x143] = 0x00;
        rom[0x147] = 0x03;  // MBC1 + RAM + battery
        rom[0x148] = (byte)romSizeCode;
        rom[0x149] = (byte)ramSizeCode;
        // Mark each bank with its bank number at offset 0 of the bank for easy verification.
        for (int bank = 0; bank < romBanks; bank++)
        {
            rom[bank * 0x4000] = (byte)bank;
            rom[bank * 0x4000 + 1] = (byte)(bank >> 8);
        }

        return CartridgeFactory.Load(rom);
    }

    [Test]
    public async Task Bank0_Reads_FromBank0()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 0);
        await Assert.That(cart.ReadRom(0x0000)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Bank1_DefaultSelected()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 0);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)1);
    }

    [Test]
    public async Task BankSelect_2_SelectsBank2()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 0);
        cart.WriteRom(0x2000, 0x02);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)2);
    }

    [Test]
    public async Task BankSelect_0_BecomesBank1()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 0);
        cart.WriteRom(0x2000, 0x00);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)1);
    }

    [Test]
    public async Task Ram_DisabledByDefault()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 1);
        await Assert.That(cart.ReadRam(0xA000)).IsEqualTo((byte)0xFF);
    }

    [Test]
    public async Task Ram_EnabledAndWriteReadRoundTrip()
    {
        var cart = MakeMbc1(romBanks: 4, ramBanks: 1);
        cart.WriteRom(0x0000, 0x0A);           // enable RAM
        cart.WriteRam(0xA000, 0x42);
        await Assert.That(cart.ReadRam(0xA000)).IsEqualTo((byte)0x42);
    }
}
