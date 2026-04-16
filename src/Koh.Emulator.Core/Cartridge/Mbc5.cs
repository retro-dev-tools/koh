namespace Koh.Emulator.Core.Cartridge;

internal static class Mbc5
{
    public static byte ReadRom(Cartridge cart, ushort address)
    {
        if (address < 0x4000)
            return address < cart.Rom.Length ? cart.Rom[address] : (byte)0xFF;
        int bank = (cart.Mbc1_BankHigh << 8) | cart.Mbc1_BankLow;
        int offset = bank * 0x4000 + (address - 0x4000);
        return offset < cart.Rom.Length ? cart.Rom[offset] : (byte)0xFF;
    }

    public static void WriteRom(Cartridge cart, ushort address, byte value)
    {
        if (address < 0x2000) { cart.Mbc1_RamEnabled = (value & 0x0F) == 0x0A; return; }
        if (address < 0x3000) { cart.Mbc1_BankLow = value; return; }
        if (address < 0x4000) { cart.Mbc1_BankHigh = (byte)(value & 0x01); return; }
        if (address < 0x6000) { cart.Mbc5_RamBank = (byte)(value & 0x0F); return; }
    }

    public static byte ReadRam(Cartridge cart, ushort address)
    {
        if (!cart.Mbc1_RamEnabled || cart.Ram.Length == 0) return 0xFF;
        int offset = cart.Mbc5_RamBank * 0x2000 + (address - 0xA000);
        return offset < cart.Ram.Length ? cart.Ram[offset] : (byte)0xFF;
    }

    public static void WriteRam(Cartridge cart, ushort address, byte value)
    {
        if (!cart.Mbc1_RamEnabled || cart.Ram.Length == 0) return;
        int offset = cart.Mbc5_RamBank * 0x2000 + (address - 0xA000);
        if (offset < cart.Ram.Length) cart.Ram[offset] = value;
    }
}
