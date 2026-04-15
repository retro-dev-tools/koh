using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Cartridge;

public sealed class Cartridge
{
    public readonly MapperKind Kind;
    public readonly CartridgeHeader Header;
    public readonly byte[] Rom;
    public readonly byte[] Ram;

    // MBC1/3/5 shared bank registers (semantics differ per mapper).
    public byte Mbc1_BankLow;      // MBC1: 5-bit; MBC3: 7-bit; MBC5: low 8 bits
    public byte Mbc1_BankHigh;     // MBC1: 2-bit; MBC3: RAM/RTC select; MBC5: bit 9
    public bool Mbc1_RamEnabled;
    public byte Mbc1_Mode;         // MBC1 only: 0 = ROM-bank, 1 = RAM-bank

    // MBC3
    public byte Mbc3_LatchLatch;   // previous value written to $6000-$7FFF
    public Rtc Rtc;

    // MBC5
    public byte Mbc5_RamBank;      // 0..15

    internal Cartridge(CartridgeHeader header, byte[] rom, byte[] ram)
    {
        Header = header;
        Kind = header.MapperKind;
        Rom = rom;
        Ram = ram;
        Mbc1_BankLow = 1;
    }

    /// <summary>
    /// Current active ROM bank for addresses in the $4000..$7FFF range. Used
    /// by the debugger to resolve banked breakpoint addresses against PC.
    /// </summary>
    public byte CurrentRomBank
    {
        get
        {
            switch (Kind)
            {
                case MapperKind.RomOnly: return 1;
                case MapperKind.Mbc1:
                    {
                        int bank = (Mbc1_BankHigh << 5) | (Mbc1_BankLow & 0x1F);
                        if ((bank & 0x1F) == 0) bank |= 1;
                        return (byte)bank;
                    }
                case MapperKind.Mbc3:
                    {
                        int bank = Mbc1_BankLow & 0x7F;
                        return (byte)(bank == 0 ? 1 : bank);
                    }
                case MapperKind.Mbc5:
                    return (byte)(((Mbc1_BankHigh << 8) | Mbc1_BankLow) & 0xFF);
                default: return 1;
            }
        }
    }

    public byte ReadRom(ushort address) => Kind switch
    {
        MapperKind.RomOnly => address < Rom.Length ? Rom[address] : (byte)0xFF,
        MapperKind.Mbc1 => Mbc1.ReadRom(this, address),
        MapperKind.Mbc3 => Mbc3.ReadRom(this, address),
        MapperKind.Mbc5 => Mbc5.ReadRom(this, address),
        _ => 0xFF,
    };

    public void WriteRom(ushort address, byte value)
    {
        switch (Kind)
        {
            case MapperKind.Mbc1: Mbc1.WriteRom(this, address, value); break;
            case MapperKind.Mbc3: Mbc3.WriteRom(this, address, value); break;
            case MapperKind.Mbc5: Mbc5.WriteRom(this, address, value); break;
        }
    }

    public byte ReadRam(ushort address) => Kind switch
    {
        MapperKind.Mbc1 => Mbc1.ReadRam(this, address),
        MapperKind.Mbc3 => Mbc3.ReadRam(this, address),
        MapperKind.Mbc5 => Mbc5.ReadRam(this, address),
        _ => 0xFF,
    };

    public void WriteRam(ushort address, byte value)
    {
        switch (Kind)
        {
            case MapperKind.Mbc1: Mbc1.WriteRam(this, address, value); break;
            case MapperKind.Mbc3: Mbc3.WriteRam(this, address, value); break;
            case MapperKind.Mbc5: Mbc5.WriteRam(this, address, value); break;
        }
    }

    public void WriteState(StateWriter w)
    {
        w.WriteI32(Ram.Length);
        if (Ram.Length > 0) w.WriteBytes(Ram);
        w.WriteByte(Mbc1_BankLow);
        w.WriteByte(Mbc1_BankHigh);
        w.WriteBool(Mbc1_RamEnabled);
        w.WriteByte(Mbc1_Mode);
        w.WriteByte(Mbc3_LatchLatch);
        Rtc.WriteState(w);
        w.WriteByte(Mbc5_RamBank);
    }

    public void ReadState(StateReader r)
    {
        int ramLen = r.ReadI32();
        if (ramLen != Ram.Length) throw new InvalidDataException("cartridge RAM size mismatch");
        if (ramLen > 0) r.ReadBytes(Ram.AsSpan());
        Mbc1_BankLow = r.ReadByte();
        Mbc1_BankHigh = r.ReadByte();
        Mbc1_RamEnabled = r.ReadBool();
        Mbc1_Mode = r.ReadByte();
        Mbc3_LatchLatch = r.ReadByte();
        Rtc.ReadState(r);
        Mbc5_RamBank = r.ReadByte();
    }
}
