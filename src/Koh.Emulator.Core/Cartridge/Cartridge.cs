using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Cartridge;

public sealed class Cartridge
{
    public readonly MapperKind Kind;
    public readonly CartridgeHeader Header;
    public readonly byte[] Rom;
    public readonly byte[] Ram;

    // MBC1 state
    public byte Mbc1_BankLow;      // 5-bit bank low (1..31)
    public byte Mbc1_BankHigh;     // 2-bit bank high (0..3)
    public bool Mbc1_RamEnabled;
    public byte Mbc1_Mode;         // 0 = ROM-bank mode, 1 = RAM-bank mode

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
            if (Kind == MapperKind.RomOnly) return 1;
            if (Kind == MapperKind.Mbc1)
            {
                int bank = (Mbc1_BankHigh << 5) | (Mbc1_BankLow & 0x1F);
                if ((bank & 0x1F) == 0) bank |= 1;
                return (byte)bank;
            }
            return 1;
        }
    }

    public byte ReadRom(ushort address)
    {
        switch (Kind)
        {
            case MapperKind.RomOnly:
                return address < Rom.Length ? Rom[address] : (byte)0xFF;
            case MapperKind.Mbc1:
                return Mbc1.ReadRom(this, address);
            default:
                return 0xFF;
        }
    }

    public void WriteRom(ushort address, byte value)
    {
        switch (Kind)
        {
            case MapperKind.RomOnly:
                // ROM is read-only; writes are dropped.
                break;
            case MapperKind.Mbc1:
                Mbc1.WriteRom(this, address, value);
                break;
        }
    }

    public byte ReadRam(ushort address)
    {
        switch (Kind)
        {
            case MapperKind.RomOnly:
                return 0xFF;
            case MapperKind.Mbc1:
                return Mbc1.ReadRam(this, address);
            default:
                return 0xFF;
        }
    }

    public void WriteRam(ushort address, byte value)
    {
        switch (Kind)
        {
            case MapperKind.RomOnly:
                break;
            case MapperKind.Mbc1:
                Mbc1.WriteRam(this, address, value);
                break;
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
    }
}
