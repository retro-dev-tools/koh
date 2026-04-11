namespace Koh.Emulator.Core.Cpu;

public struct CpuRegisters
{
    public byte A;
    public byte F;
    public byte B;
    public byte C;
    public byte D;
    public byte E;
    public byte H;
    public byte L;
    public ushort Sp;
    public ushort Pc;

    public const byte FlagZ = 0x80;
    public const byte FlagN = 0x40;
    public const byte FlagH = 0x20;
    public const byte FlagC = 0x10;

    public ushort AF
    {
        readonly get => (ushort)((A << 8) | (F & 0xF0));
        set { A = (byte)(value >> 8); F = (byte)(value & 0xF0); }
    }

    public ushort BC
    {
        readonly get => (ushort)((B << 8) | C);
        set { B = (byte)(value >> 8); C = (byte)(value & 0xFF); }
    }

    public ushort DE
    {
        readonly get => (ushort)((D << 8) | E);
        set { D = (byte)(value >> 8); E = (byte)(value & 0xFF); }
    }

    public ushort HL
    {
        readonly get => (ushort)((H << 8) | L);
        set { H = (byte)(value >> 8); L = (byte)(value & 0xFF); }
    }

    public readonly bool FlagSet(byte mask) => (F & mask) != 0;
    public void SetFlag(byte mask, bool on) => F = on ? (byte)(F | mask) : (byte)(F & ~mask);
}
