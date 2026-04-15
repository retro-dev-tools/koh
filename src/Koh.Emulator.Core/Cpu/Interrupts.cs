namespace Koh.Emulator.Core.Cpu;

public struct Interrupts
{
    public byte IF;             // $FF0F — interrupt flag
    public byte IE;             // $FFFF — interrupt enable

    public const byte VBlank = 1 << 0;
    public const byte Stat   = 1 << 1;
    public const byte Timer  = 1 << 2;
    public const byte Serial = 1 << 3;
    public const byte Joypad = 1 << 4;

    public readonly byte Pending => (byte)(IF & IE & 0x1F);
    public readonly bool HasPending => Pending != 0;

    public void Raise(byte interrupt) => IF |= interrupt;
    public void Clear(byte interrupt) => IF &= (byte)~interrupt;
}
