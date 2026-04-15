using Koh.Emulator.Core.Cartridge;

namespace Koh.Emulator.Core.Tests;

public class CartridgeHeaderTests
{
    private static byte[] MakeHeader(byte cartType, byte romSize, byte ramSize, byte cgbFlag, string title)
    {
        var rom = new byte[0x150];
        var titleBytes = System.Text.Encoding.ASCII.GetBytes(title);
        titleBytes.AsSpan(0, Math.Min(titleBytes.Length, 15)).CopyTo(rom.AsSpan(0x134));
        rom[0x143] = cgbFlag;
        rom[0x147] = cartType;
        rom[0x148] = romSize;
        rom[0x149] = ramSize;
        return rom;
    }

    [Test]
    public async Task Parse_RomOnly_16KB()
    {
        var rom = MakeHeader(cartType: 0x00, romSize: 0x00, ramSize: 0x00, cgbFlag: 0x00, title: "TEST");
        var header = CartridgeHeader.Parse(rom);

        await Assert.That(header.MapperKind).IsEqualTo(MapperKind.RomOnly);
        await Assert.That(header.RomBanks).IsEqualTo(2);
        await Assert.That(header.RamBanks).IsEqualTo(0);
        await Assert.That(header.CgbFlag).IsFalse();
        await Assert.That(header.Title).IsEqualTo("TEST");
    }

    [Test]
    public async Task Parse_Mbc1_WithRam()
    {
        var rom = MakeHeader(cartType: 0x03, romSize: 0x03, ramSize: 0x03, cgbFlag: 0x00, title: "MBC1TEST");
        var header = CartridgeHeader.Parse(rom);

        await Assert.That(header.MapperKind).IsEqualTo(MapperKind.Mbc1);
        await Assert.That(header.RomBanks).IsEqualTo(16);
        await Assert.That(header.RamBanks).IsEqualTo(4);
    }

    [Test]
    public async Task Parse_CgbFlag_Detected()
    {
        var rom = MakeHeader(cartType: 0x00, romSize: 0x00, ramSize: 0x00, cgbFlag: 0x80, title: "CGB");
        var header = CartridgeHeader.Parse(rom);

        await Assert.That(header.CgbFlag).IsTrue();
        await Assert.That(header.CgbOnly).IsFalse();
    }

    [Test]
    public async Task Parse_CgbOnly_Detected()
    {
        var rom = MakeHeader(cartType: 0x00, romSize: 0x00, ramSize: 0x00, cgbFlag: 0xC0, title: "CGBO");
        var header = CartridgeHeader.Parse(rom);

        await Assert.That(header.CgbOnly).IsTrue();
    }

    [Test]
    public async Task Parse_UnsupportedCartType_Throws()
    {
        var rom = MakeHeader(cartType: 0xFF, romSize: 0x00, ramSize: 0x00, cgbFlag: 0x00, title: "BAD");
        await Assert.That(() => CartridgeHeader.Parse(rom)).Throws<NotSupportedException>();
    }
}
