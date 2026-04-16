namespace Koh.Emulator.Core.Cartridge;

internal static class Mbc3
{
    public static byte ReadRom(Cartridge cart, ushort address)
    {
        if (address < 0x4000)
            return address < cart.Rom.Length ? cart.Rom[address] : (byte)0xFF;
        int bank = cart.Mbc1_BankLow & 0x7F;
        if (bank == 0) bank = 1;
        int offset = bank * 0x4000 + (address - 0x4000);
        return offset < cart.Rom.Length ? cart.Rom[offset] : (byte)0xFF;
    }

    public static byte ReadRam(Cartridge cart, ushort address)
    {
        if (!cart.Mbc1_RamEnabled) return 0xFF;
        byte sel = cart.Mbc1_BankHigh;
        if (sel < 0x04)
        {
            if (cart.Ram.Length == 0) return 0xFF;
            int offset = sel * 0x2000 + (address - 0xA000);
            return offset < cart.Ram.Length ? cart.Ram[offset] : (byte)0xFF;
        }
        return sel switch
        {
            0x08 => cart.Rtc.LatchedSeconds,
            0x09 => cart.Rtc.LatchedMinutes,
            0x0A => cart.Rtc.LatchedHours,
            0x0B => cart.Rtc.LatchedDayLow,
            0x0C => cart.Rtc.LatchedDayHighAndFlags,
            _ => 0xFF,
        };
    }

    public static void WriteRom(Cartridge cart, ushort address, byte value)
    {
        if (address < 0x2000) { cart.Mbc1_RamEnabled = (value & 0x0F) == 0x0A; return; }
        if (address < 0x4000) { cart.Mbc1_BankLow = (byte)(value & 0x7F); return; }
        if (address < 0x6000) { cart.Mbc1_BankHigh = value; return; }

        // $6000-$7FFF: RTC latch. 0x00 followed by 0x01 latches.
        if (cart.Mbc3_LatchLatch == 0x00 && value == 0x01)
        {
            cart.Rtc.AdvanceFromHost(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cart.Rtc.Latch();
        }
        cart.Mbc3_LatchLatch = value;
    }

    public static void WriteRam(Cartridge cart, ushort address, byte value)
    {
        if (!cart.Mbc1_RamEnabled) return;
        byte sel = cart.Mbc1_BankHigh;
        if (sel < 0x04)
        {
            if (cart.Ram.Length == 0) return;
            int offset = sel * 0x2000 + (address - 0xA000);
            if (offset < cart.Ram.Length) cart.Ram[offset] = value;
            return;
        }
        switch (sel)
        {
            case 0x08: cart.Rtc.Seconds = (byte)(value & 0x3F); break;
            case 0x09: cart.Rtc.Minutes = (byte)(value & 0x3F); break;
            case 0x0A: cart.Rtc.Hours = (byte)(value & 0x1F); break;
            case 0x0B: cart.Rtc.DayLow = value; break;
            case 0x0C: cart.Rtc.DayHighAndFlags = (byte)(value & 0xC1); break;
        }
    }
}
