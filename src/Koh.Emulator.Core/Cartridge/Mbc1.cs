namespace Koh.Emulator.Core.Cartridge;

internal static class Mbc1
{
    public static byte ReadRom(Cartridge cart, ushort address)
    {
        if (address < 0x4000)
        {
            // Bank 0 area. In MBC1 mode 1 with large ROMs, the high 2 bits
            // of the bank register map this window to banks $20/$40/$60.
            int bank0 = cart.Mbc1_Mode == 1 ? (cart.Mbc1_BankHigh << 5) : 0;
            int offset = (bank0 * 0x4000) + address;
            return offset < cart.Rom.Length ? cart.Rom[offset] : (byte)0xFF;
        }
        else
        {
            // $4000-$7FFF switchable ROM bank.
            int low = cart.Mbc1_BankLow & 0x1F;
            if (low == 0) low = 1;  // MBC1 quirk: bank 0 selects bank 1
            int bank = (cart.Mbc1_BankHigh << 5) | low;
            int offset = (bank * 0x4000) + (address - 0x4000);
            return offset < cart.Rom.Length ? cart.Rom[offset] : (byte)0xFF;
        }
    }

    public static void WriteRom(Cartridge cart, ushort address, byte value)
    {
        if (address < 0x2000)
        {
            // RAM enable: lower 4 bits == 0xA enables.
            cart.Mbc1_RamEnabled = (value & 0x0F) == 0x0A;
        }
        else if (address < 0x4000)
        {
            // ROM bank low (5 bits).
            byte low = (byte)(value & 0x1F);
            cart.Mbc1_BankLow = low == 0 ? (byte)1 : low;
        }
        else if (address < 0x6000)
        {
            // RAM bank / ROM bank high (2 bits).
            cart.Mbc1_BankHigh = (byte)(value & 0x03);
        }
        else
        {
            // Banking mode select.
            cart.Mbc1_Mode = (byte)(value & 0x01);
        }
    }

    public static byte ReadRam(Cartridge cart, ushort address)
    {
        if (!cart.Mbc1_RamEnabled || cart.Ram.Length == 0) return 0xFF;
        int bank = cart.Mbc1_Mode == 1 ? cart.Mbc1_BankHigh : 0;
        int offset = (bank * 0x2000) + (address - 0xA000);
        return offset < cart.Ram.Length ? cart.Ram[offset] : (byte)0xFF;
    }

    public static void WriteRam(Cartridge cart, ushort address, byte value)
    {
        if (!cart.Mbc1_RamEnabled || cart.Ram.Length == 0) return;
        int bank = cart.Mbc1_Mode == 1 ? cart.Mbc1_BankHigh : 0;
        int offset = (bank * 0x2000) + (address - 0xA000);
        if (offset < cart.Ram.Length) cart.Ram[offset] = value;
    }
}
