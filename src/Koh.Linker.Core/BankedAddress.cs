namespace Koh.Linker.Core;

public readonly record struct BankedAddress(byte Bank, ushort Address)
{
    public uint Packed => ((uint)Bank << 16) | Address;
}
