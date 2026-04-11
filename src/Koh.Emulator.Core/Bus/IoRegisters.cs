using Koh.Emulator.Core.Cpu;

namespace Koh.Emulator.Core.Bus;

/// <summary>
/// $FF00-$FF7F I/O dispatch. Phase 1 implements only the registers needed
/// for Timer + Interrupts. Other registers read/write a backing byte array
/// without side effects; Phase 2 wires PPU registers.
/// </summary>
public sealed class IoRegisters
{
    private readonly byte[] _io = new byte[0x80];

    public Timer.Timer Timer { get; }
    private Interrupts _interrupts;

    public ref Interrupts Interrupts => ref _interrupts;

    public IoRegisters(Timer.Timer timer)
    {
        Timer = timer;
    }

    public byte Read(ushort address)
    {
        int idx = address - 0xFF00;
        if (idx < 0 || idx >= _io.Length) return 0xFF;

        return address switch
        {
            0xFF04 => Timer.DIV,
            0xFF05 => Timer.TIMA,
            0xFF06 => Timer.TMA,
            0xFF07 => (byte)(Timer.TAC | 0xF8),
            0xFF0F => (byte)(_interrupts.IF | 0xE0),
            _ => _io[idx],
        };
    }

    public void Write(ushort address, byte value)
    {
        int idx = address - 0xFF00;
        if (idx < 0 || idx >= _io.Length) return;

        switch (address)
        {
            case 0xFF04: Timer.WriteDiv(); break;
            case 0xFF05: Timer.WriteTima(value); break;
            case 0xFF06: Timer.WriteTma(value); break;
            case 0xFF07: Timer.WriteTac(value); break;
            case 0xFF0F: _interrupts.IF = (byte)(value & 0x1F); break;
            default:     _io[idx] = value; break;
        }
    }

    public byte ReadIe() => _interrupts.IE;
    public void WriteIe(byte value) => _interrupts.IE = (byte)(value & 0x1F);
}
