using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class Mbc3Tests
{
    private static Koh.Emulator.Core.Cartridge.Cartridge MakeCart(int romBanks = 4, int ramSizeCode = 0x02)
    {
        var rom = new byte[romBanks * 0x4000];
        rom[0x147] = 0x13;  // MBC3 + RAM + battery
        rom[0x148] = romBanks switch { 2 => 0x00, 4 => 0x01, 8 => 0x02, 16 => 0x03, _ => 0x01 };
        rom[0x149] = (byte)ramSizeCode;
        // Marker byte at bank 0 $0000 and bank 1 $4000.
        rom[0x0000] = 0xA0;
        rom[0x4000] = 0xA1;
        if (romBanks >= 3) rom[0x8000] = 0xA2;   // bank 2
        return CartridgeFactory.Load(rom);
    }

    [Test]
    public async Task Bank_Switch_Selects_Correct_Rom_Bank()
    {
        var cart = MakeCart(romBanks: 4);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)0xA1);   // default bank 1
        cart.WriteRom(0x2000, 0x02);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)0xA2);
    }

    [Test]
    public async Task Bank_Zero_Write_Remains_At_Bank_One()
    {
        var cart = MakeCart(romBanks: 4);
        cart.WriteRom(0x2000, 0x00);
        await Assert.That(cart.ReadRom(0x4000)).IsEqualTo((byte)0xA1);
    }

    [Test]
    public async Task Ram_Is_Gated_By_Enable_Register()
    {
        var cart = MakeCart();
        // Disabled by default.
        cart.WriteRam(0xA000, 0x77);
        await Assert.That(cart.ReadRam(0xA000)).IsEqualTo((byte)0xFF);
        // Enable + write + read.
        cart.WriteRom(0x0000, 0x0A);
        cart.WriteRam(0xA000, 0x77);
        await Assert.That(cart.ReadRam(0xA000)).IsEqualTo((byte)0x77);
    }

    [Test]
    public async Task Rtc_Latch_Round_Trip()
    {
        var cart = MakeCart();
        cart.WriteRom(0x0000, 0x0A);   // enable RAM+RTC

        // Select RTC seconds register.
        cart.WriteRom(0x4000, 0x08);
        cart.WriteRam(0xA000, 0x2A);   // seconds = 42 (within 0..59 mask)

        // Latch sequence 0 → 1.
        cart.WriteRom(0x6000, 0x00);
        cart.WriteRom(0x6000, 0x01);

        await Assert.That(cart.ReadRam(0xA000)).IsEqualTo((byte)0x2A);
    }

    [Test]
    public async Task Rtc_Halted_Does_Not_Advance()
    {
        var cart = MakeCart();
        cart.Rtc.Seconds = 30;
        cart.Rtc.DayHighAndFlags = 0x40;   // halt bit
        cart.Rtc.BaseUnixSeconds = 1000;
        cart.Rtc.AdvanceFromHost(9999);
        await Assert.That(cart.Rtc.Seconds).IsEqualTo((byte)30);
    }

    [Test]
    public async Task Rtc_Advance_Rolls_Over_Minutes()
    {
        var cart = MakeCart();
        cart.Rtc.Seconds = 50;
        cart.Rtc.Minutes = 10;
        cart.Rtc.BaseUnixSeconds = 1000;
        cart.Rtc.AdvanceFromHost(1020);   // +20 s → 70 s total → Minutes = 11, Seconds = 10
        await Assert.That(cart.Rtc.Minutes).IsEqualTo((byte)11);
        await Assert.That(cart.Rtc.Seconds).IsEqualTo((byte)10);
    }
}
