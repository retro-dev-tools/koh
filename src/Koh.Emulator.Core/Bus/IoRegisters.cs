using Koh.Emulator.Core.Cgb;
using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Dma;
using Koh.Emulator.Core.Ppu;

namespace Koh.Emulator.Core.Bus;

/// <summary>
/// $FF00-$FF7F I/O dispatch. Phase 2 wires PPU ($FF40-$FF4B, $FF68-$FF6B),
/// HDMA ($FF51-$FF55), KEY1 ($FF4D), and CGB banking ($FF4F, $FF70) registers.
/// </summary>
public sealed class IoRegisters
{
    private readonly byte[] _io = new byte[0x80];

    public Timer.Timer Timer { get; }
    private Interrupts _interrupts;

    public ref Interrupts Interrupts => ref _interrupts;

    private Ppu.Ppu? _ppu;
    private Hdma? _hdma;
    private KeyOneRegister? _keyOne;
    private VramWramBanking? _banking;

    public IoRegisters(Timer.Timer timer)
    {
        Timer = timer;
    }

    public void AttachPpu(Ppu.Ppu ppu) => _ppu = ppu;
    public void AttachHdma(Hdma hdma) => _hdma = hdma;
    public void AttachKeyOne(KeyOneRegister keyOne) => _keyOne = keyOne;
    public void AttachBanking(VramWramBanking banking) => _banking = banking;

    public byte Read(ushort address)
    {
        int idx = address - 0xFF00;
        if (idx < 0 || idx >= _io.Length) return 0xFF;

        switch (address)
        {
            case 0xFF04: return Timer.DIV;
            case 0xFF05: return Timer.TIMA;
            case 0xFF06: return Timer.TMA;
            case 0xFF07: return (byte)(Timer.TAC | 0xF8);
            case 0xFF0F: return (byte)(_interrupts.IF | 0xE0);

            // PPU registers
            case 0xFF40: return _ppu?.LCDC ?? _io[idx];
            case 0xFF41: return _ppu is null ? _io[idx] : _ppu.Stat.Read(_ppu.Mode, _ppu.LY == _ppu.LYC);
            case 0xFF42: return _ppu?.SCY ?? _io[idx];
            case 0xFF43: return _ppu?.SCX ?? _io[idx];
            case 0xFF44: return _ppu?.LY ?? _io[idx];
            case 0xFF45: return _ppu?.LYC ?? _io[idx];
            case 0xFF47: return _ppu?.BGP ?? _io[idx];
            case 0xFF48: return _ppu?.OBP0 ?? _io[idx];
            case 0xFF49: return _ppu?.OBP1 ?? _io[idx];
            case 0xFF4A: return _ppu?.WY ?? _io[idx];
            case 0xFF4B: return _ppu?.WX ?? _io[idx];

            // CGB banking + KEY1
            case 0xFF4D: return _keyOne?.Read() ?? 0xFF;
            case 0xFF4F: return _banking?.ReadVbkRegister() ?? 0xFF;
            case 0xFF70: return _banking?.ReadSvbkRegister() ?? 0xFF;

            // HDMA
            case 0xFF51: return _hdma?.Source1 ?? 0xFF;
            case 0xFF52: return _hdma?.Source2 ?? 0xFF;
            case 0xFF53: return _hdma?.Dest1 ?? 0xFF;
            case 0xFF54: return _hdma?.Dest2 ?? 0xFF;
            case 0xFF55: return _hdma?.ReadLengthRegister() ?? 0xFF;

            // CGB palette
            case 0xFF68: return _ppu is null ? _io[idx] : _ppu.BgPalette.IndexRegister;
            case 0xFF69: return _ppu?.BgPalette.ReadData() ?? 0xFF;
            case 0xFF6A: return _ppu is null ? _io[idx] : _ppu.ObjPalette.IndexRegister;
            case 0xFF6B: return _ppu?.ObjPalette.ReadData() ?? 0xFF;
            case 0xFF6C: return _ppu is null ? _io[idx] : (byte)(_ppu.OPRI | 0xFE);

            default: return _io[idx];
        }
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

            // PPU registers
            case 0xFF40: if (_ppu is not null) _ppu.LCDC = value; else _io[idx] = value; break;
            case 0xFF41: if (_ppu is not null) _ppu.Stat.Write(value); else _io[idx] = value; break;
            case 0xFF42: if (_ppu is not null) _ppu.SCY = value; else _io[idx] = value; break;
            case 0xFF43: if (_ppu is not null) _ppu.SCX = value; else _io[idx] = value; break;
            case 0xFF44: break;  // LY is read-only
            case 0xFF45: if (_ppu is not null) _ppu.LYC = value; else _io[idx] = value; break;
            case 0xFF47: if (_ppu is not null) _ppu.BGP = value; else _io[idx] = value; break;
            case 0xFF48: if (_ppu is not null) _ppu.OBP0 = value; else _io[idx] = value; break;
            case 0xFF49: if (_ppu is not null) _ppu.OBP1 = value; else _io[idx] = value; break;
            case 0xFF4A: if (_ppu is not null) _ppu.WY = value; else _io[idx] = value; break;
            case 0xFF4B: if (_ppu is not null) _ppu.WX = value; else _io[idx] = value; break;

            // CGB banking + KEY1
            case 0xFF4D: _keyOne?.Write(value); break;
            case 0xFF4F: _banking?.WriteVbkRegister(value); break;
            case 0xFF70: _banking?.WriteSvbkRegister(value); break;

            // HDMA
            case 0xFF51: if (_hdma is not null) _hdma.Source1 = value; break;
            case 0xFF52: if (_hdma is not null) _hdma.Source2 = value; break;
            case 0xFF53: if (_hdma is not null) _hdma.Dest1 = value; break;
            case 0xFF54: if (_hdma is not null) _hdma.Dest2 = value; break;
            case 0xFF55: _hdma?.WriteLengthRegister(value); break;

            // CGB palette
            case 0xFF68: if (_ppu is not null) _ppu.BgPalette.IndexRegister = value; else _io[idx] = value; break;
            case 0xFF69: _ppu?.BgPalette.WriteData(value); break;
            case 0xFF6A: if (_ppu is not null) _ppu.ObjPalette.IndexRegister = value; else _io[idx] = value; break;
            case 0xFF6B: _ppu?.ObjPalette.WriteData(value); break;
            case 0xFF6C: if (_ppu is not null) _ppu.OPRI = (byte)(value & 1); break;

            default: _io[idx] = value; break;
        }
    }

    public byte ReadIe() => _interrupts.IE;
    public void WriteIe(byte value) => _interrupts.IE = (byte)(value & 0x1F);
}
