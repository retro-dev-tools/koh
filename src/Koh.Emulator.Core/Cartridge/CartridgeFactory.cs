namespace Koh.Emulator.Core.Cartridge;

public static class CartridgeFactory
{
    public static Cartridge Load(ReadOnlySpan<byte> romBytes)
    {
        var header = CartridgeHeader.Parse(romBytes);
        var rom = romBytes.ToArray();
        var ram = new byte[header.RamBanks * 0x2000];
        return new Cartridge(header, rom, ram);
    }
}
