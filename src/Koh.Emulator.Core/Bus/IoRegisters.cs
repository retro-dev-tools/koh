using Koh.Emulator.Core.Apu;
using Koh.Emulator.Core.Cgb;
using Koh.Emulator.Core.Cpu;
using Koh.Emulator.Core.Dma;
using Koh.Emulator.Core.Joypad;
using Koh.Emulator.Core.Ppu;
using Koh.Emulator.Core.Serial;
using Koh.Emulator.Core.State;

namespace Koh.Emulator.Core.Bus;

/// <summary>
/// $FF00-$FF7F I/O dispatch. Phase 2 wires PPU ($FF40-$FF4B, $FF68-$FF6B),
/// HDMA ($FF51-$FF55), KEY1 ($FF4D), and CGB banking ($FF4F, $FF70) registers.
/// </summary>
public sealed class IoRegisters
{
    private readonly byte[] _io = new byte[0x80];

    public Timer.Timer Timer { get; }
    public Serial.Serial Serial { get; } = new();
    private Interrupts _interrupts;

    public ref Interrupts Interrupts => ref _interrupts;

    private Ppu.Ppu? _ppu;
    private Hdma? _hdma;
    private KeyOneRegister? _keyOne;
    private VramWramBanking? _banking;
    private Apu.Apu? _apu;

    public HardwareMode HardwareMode { get; set; } = HardwareMode.Dmg;

    public IoRegisters(Timer.Timer timer)
    {
        Timer = timer;
    }

    private bool IsCgb => HardwareMode == HardwareMode.Cgb;

    public void AttachPpu(Ppu.Ppu ppu) => _ppu = ppu;
    public void AttachHdma(Hdma hdma) => _hdma = hdma;
    public void AttachKeyOne(KeyOneRegister keyOne) => _keyOne = keyOne;

    /// <summary>
    /// Exposed so the CPU can consult the KEY1 switch-armed flag from inside
    /// the STOP instruction handler — a CGB-only flow.
    /// </summary>
    public KeyOneRegister? KeyOne => _keyOne;

    public void AttachBanking(VramWramBanking banking) => _banking = banking;
    public void AttachApu(Apu.Apu apu) => _apu = apu;

    private Func<JoypadState>? _readJoypad;
    public void AttachJoypad(Func<JoypadState> readJoypad) => _readJoypad = readJoypad;

    public byte Read(ushort address)
    {
        int idx = address - 0xFF00;
        if (idx < 0 || idx >= _io.Length) return 0xFF;

        // APU range ($FF10-$FF3F) delegates to the APU when attached; otherwise
        // the legacy reserved-bit masks below apply.
        if (_apu is not null && address >= 0xFF10 && address <= 0xFF3F)
            return _apu.Read(address);

        switch (address)
        {
            // JOYP ($FF00): bits 6-7 always 1. Buttons are active-low.
            // Bits 4-5 select which group: bit 5=0 → action buttons (A/B/Select/Start),
            // bit 4=0 → direction buttons (Right/Left/Up/Down).
            case 0xFF00:
                {
                    byte selectMask = (byte)(_io[0] & 0x30);
                    byte pressed = 0;
                    if (_readJoypad is not null)
                    {
                        var j = _readJoypad();
                        if ((selectMask & 0x10) == 0)
                        {
                            if (j.IsPressed(JoypadButton.Right)) pressed |= 0x01;
                            if (j.IsPressed(JoypadButton.Left))  pressed |= 0x02;
                            if (j.IsPressed(JoypadButton.Up))    pressed |= 0x04;
                            if (j.IsPressed(JoypadButton.Down))  pressed |= 0x08;
                        }
                        if ((selectMask & 0x20) == 0)
                        {
                            if (j.IsPressed(JoypadButton.A))      pressed |= 0x01;
                            if (j.IsPressed(JoypadButton.B))      pressed |= 0x02;
                            if (j.IsPressed(JoypadButton.Select)) pressed |= 0x04;
                            if (j.IsPressed(JoypadButton.Start))  pressed |= 0x08;
                        }
                    }
                    byte lower = (byte)(~pressed & 0x0F);   // active-low
                    return (byte)(0xC0 | selectMask | lower);
                }
            case 0xFF01: return Serial.ReadSB();
            case 0xFF02: return Serial.ReadSC();
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

            // CGB-only registers read $FF on DMG.
            case 0xFF4D: return IsCgb ? (_keyOne?.Read() ?? 0xFF) : (byte)0xFF;
            case 0xFF4F: return IsCgb ? (_banking?.ReadVbkRegister() ?? 0xFF) : (byte)0xFF;
            case 0xFF70: return IsCgb ? (_banking?.ReadSvbkRegister() ?? 0xFF) : (byte)0xFF;

            case 0xFF51: return IsCgb ? (_hdma?.Source1 ?? 0xFF) : (byte)0xFF;
            case 0xFF52: return IsCgb ? (_hdma?.Source2 ?? 0xFF) : (byte)0xFF;
            case 0xFF53: return IsCgb ? (_hdma?.Dest1 ?? 0xFF) : (byte)0xFF;
            case 0xFF54: return IsCgb ? (_hdma?.Dest2 ?? 0xFF) : (byte)0xFF;
            case 0xFF55: return IsCgb ? (_hdma?.ReadLengthRegister() ?? 0xFF) : (byte)0xFF;

            case 0xFF68: return IsCgb && _ppu is not null ? _ppu.BgPalette.IndexRegister : (byte)0xFF;
            case 0xFF69: return IsCgb ? (_ppu?.BgPalette.ReadData() ?? 0xFF) : (byte)0xFF;
            case 0xFF6A: return IsCgb && _ppu is not null ? _ppu.ObjPalette.IndexRegister : (byte)0xFF;
            case 0xFF6B: return IsCgb ? (_ppu?.ObjPalette.ReadData() ?? 0xFF) : (byte)0xFF;
            case 0xFF6C: return IsCgb && _ppu is not null ? (byte)(_ppu.OPRI | 0xFE) : (byte)0xFF;

            // APU registers have a fixed "unused bits read as 1" pattern per
            // pandocs. Until Phase 4 implements the APU, return stored values
            // OR'd with the appropriate masks.
            case 0xFF10: return (byte)(_io[idx] | 0x80);    // NR10: bit 7 = 1
            case 0xFF11: return (byte)(_io[idx] | 0x3F);    // NR11: bits 0-5 = 1 (only bits 6-7 readable)
            case 0xFF13: return 0xFF;                       // NR13: write-only
            case 0xFF14: return (byte)(_io[idx] | 0xBF);    // NR14: bits 0-5, bit 7 = 1 (only bit 6 readable)
            case 0xFF15: return 0xFF;                       // unused
            case 0xFF16: return (byte)(_io[idx] | 0x3F);    // NR21
            case 0xFF18: return 0xFF;                       // NR23: write-only
            case 0xFF19: return (byte)(_io[idx] | 0xBF);    // NR24
            case 0xFF1A: return (byte)(_io[idx] | 0x7F);    // NR30: bits 0-6 = 1
            case 0xFF1B: return 0xFF;                       // NR31: write-only
            case 0xFF1C: return (byte)(_io[idx] | 0x9F);    // NR32: bits 0-4, bit 7 = 1
            case 0xFF1D: return 0xFF;                       // NR33: write-only
            case 0xFF1E: return (byte)(_io[idx] | 0xBF);    // NR34
            case 0xFF1F: return 0xFF;                       // unused
            case 0xFF20: return 0xFF;                       // NR41: write-only
            case 0xFF23: return (byte)(_io[idx] | 0xBF);    // NR44
            case 0xFF26: return (byte)(_io[idx] | 0x70);    // NR52: bits 4-6 = 1

            // Ports in the \$FF27-\$FF2F range are unused and read \$FF.
            case 0xFF27: case 0xFF28: case 0xFF29: case 0xFF2A:
            case 0xFF2B: case 0xFF2C: case 0xFF2D: case 0xFF2E: case 0xFF2F:
                return 0xFF;

            // BCPS / OCPS index registers: bit 6 reads as 1.
            // (Already returned via _ppu accessors above — this branch not reached.)

            default:
                // Unmapped I/O ports read as \$FF on real hardware.
                if (IsUnmappedIoPort(address)) return 0xFF;
                return _io[idx];
        }
    }

    private static bool IsUnmappedIoPort(ushort address) => address switch
    {
        0xFF03 => true,
        >= 0xFF08 and <= 0xFF0E => true,
        0xFF4C => true,
        0xFF4E => true,
        0xFF50 => true,                              // BANK (boot ROM unmap) — write-only
        >= 0xFF57 and <= 0xFF67 => true,
        >= 0xFF6D and <= 0xFF6F => true,
        >= 0xFF71 and <= 0xFF7F => true,
        _ => false,
    };

    public void Write(ushort address, byte value)
    {
        int idx = address - 0xFF00;
        if (idx < 0 || idx >= _io.Length) return;

        if (_apu is not null && address >= 0xFF10 && address <= 0xFF3F)
        {
            _apu.Write(address, value);
            return;
        }

        switch (address)
        {
            // JOYP: only bits 4-5 (button-group selection) are writable.
            case 0xFF00: _io[0] = (byte)(value & 0x30); break;
            case 0xFF01: Serial.WriteSB(value); break;
            case 0xFF02: Serial.WriteSC(value); break;
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

            // CGB banking + KEY1 (inert on DMG)
            case 0xFF4D: if (IsCgb) _keyOne?.Write(value); break;
            case 0xFF4F: if (IsCgb) _banking?.WriteVbkRegister(value); break;
            case 0xFF70: if (IsCgb) _banking?.WriteSvbkRegister(value); break;

            // HDMA (inert on DMG)
            case 0xFF51: if (IsCgb && _hdma is not null) _hdma.Source1 = value; break;
            case 0xFF52: if (IsCgb && _hdma is not null) _hdma.Source2 = value; break;
            case 0xFF53: if (IsCgb && _hdma is not null) _hdma.Dest1 = value; break;
            case 0xFF54: if (IsCgb && _hdma is not null) _hdma.Dest2 = value; break;
            case 0xFF55: if (IsCgb) _hdma?.WriteLengthRegister(value); break;

            // CGB palette (inert on DMG)
            case 0xFF68: if (IsCgb && _ppu is not null) _ppu.BgPalette.IndexRegister = value; break;
            case 0xFF69: if (IsCgb) _ppu?.BgPalette.WriteData(value); break;
            case 0xFF6A: if (IsCgb && _ppu is not null) _ppu.ObjPalette.IndexRegister = value; break;
            case 0xFF6B: if (IsCgb) _ppu?.ObjPalette.WriteData(value); break;
            case 0xFF6C: if (IsCgb && _ppu is not null) _ppu.OPRI = (byte)(value & 1); break;

            default: _io[idx] = value; break;
        }
    }

    public void WriteState(StateWriter w)
    {
        w.WriteBytes(_io);
        w.WriteByte(_interrupts.IF); w.WriteByte(_interrupts.IE);
    }

    public void ReadState(StateReader r)
    {
        r.ReadBytes(_io.AsSpan());
        _interrupts.IF = r.ReadByte(); _interrupts.IE = r.ReadByte();
    }

    public byte ReadIe() => _interrupts.IE;
    // IE is a fully 8-bit-readable/writable register on DMG. Only bits 0..4
    // affect interrupt dispatch; bits 5..7 are readable but architecturally
    // unused.
    public void WriteIe(byte value) => _interrupts.IE = value;
}
