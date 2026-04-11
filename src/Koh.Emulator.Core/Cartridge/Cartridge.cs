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
}
