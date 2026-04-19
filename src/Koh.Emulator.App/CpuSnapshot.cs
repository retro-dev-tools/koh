using Koh.Emulator.Core;
using Koh.Emulator.Core.Cpu;
using KohUI.Theme;

namespace Koh.Emulator.App;

/// <summary>
/// Immutable snapshot of the CPU's register state after a frame.
/// Published by <see cref="EmulatorLoop"/> per-frame via a volatile
/// reference write so the UI thread can read a consistent set without
/// reaching into the live <see cref="GameBoySystem"/>.
/// </summary>
public sealed record CpuSnapshot(
    ushort Pc,
    ushort Sp,
    byte A,
    byte F,
    ushort BC,
    ushort DE,
    ushort HL,
    ulong TotalTCycles,
    bool FlagZ,
    bool FlagN,
    bool FlagH,
    bool FlagC)
{
    public static CpuSnapshot From(GameBoySystem sys)
    {
        ref var r = ref sys.Cpu.Registers;
        return new CpuSnapshot(
            Pc:           r.Pc,
            Sp:           r.Sp,
            A:            r.A,
            F:            r.F,
            BC:           r.BC,
            DE:           r.DE,
            HL:           r.HL,
            TotalTCycles: sys.Cpu.TotalTCycles,
            FlagZ:        r.FlagSet(CpuRegisters.FlagZ),
            FlagN:        r.FlagSet(CpuRegisters.FlagN),
            FlagH:        r.FlagSet(CpuRegisters.FlagH),
            FlagC:        r.FlagSet(CpuRegisters.FlagC));
    }
}

/// <summary>
/// Snapshot of PPU palette state. DMG and CGB share this shape: DMG
/// fills the first BG / OBJ0 / OBJ1 palettes from BGP/OBP0/OBP1 byte
/// decoding, CGB fills all 8 of each from its palette RAM. Colours are
/// packed into <see cref="KohColor"/> (RGB8) at snapshot time so the
/// UI doesn't need to know about BGR555 encoding.
/// </summary>
public sealed record PaletteSnapshot(
    bool IsCgb,
    byte Bgp,
    byte Obp0,
    byte Obp1,
    KohColor[] BgColors,    // [palette * 4 + slot]; length = 4 (DMG) or 32 (CGB)
    KohColor[] ObjColors)
{
    public static PaletteSnapshot From(GameBoySystem sys)
    {
        bool cgb = sys.Mode == HardwareMode.Cgb;
        if (cgb)
        {
            var bg = new KohColor[8 * 4];
            var obj = new KohColor[8 * 4];
            for (int p = 0; p < 8; p++)
            for (int s = 0; s < 4; s++)
            {
                bg[p * 4 + s]  = Bgr555ToRgb(sys.Ppu.BgPalette.GetColor(p, s));
                obj[p * 4 + s] = Bgr555ToRgb(sys.Ppu.ObjPalette.GetColor(p, s));
            }
            return new PaletteSnapshot(true, sys.Ppu.BGP, sys.Ppu.OBP0, sys.Ppu.OBP1, bg, obj);
        }
        return new PaletteSnapshot(false, sys.Ppu.BGP, sys.Ppu.OBP0, sys.Ppu.OBP1,
            BgColors: DmgDecode(sys.Ppu.BGP),
            ObjColors: DmgDecode(sys.Ppu.OBP0).Concat(DmgDecode(sys.Ppu.OBP1)).ToArray());
    }

    private static KohColor[] DmgDecode(byte palette)
    {
        // Two bits per slot, 00→white, 01→light grey, 10→dark grey,
        // 11→black. Green-tinted to match a real DMG screen.
        KohColor[] shades =
        [
            new(0x9b, 0xbc, 0x0f),
            new(0x8b, 0xac, 0x0f),
            new(0x30, 0x62, 0x30),
            new(0x0f, 0x38, 0x0f),
        ];
        return
        [
            shades[(palette >> 0) & 3],
            shades[(palette >> 2) & 3],
            shades[(palette >> 4) & 3],
            shades[(palette >> 6) & 3],
        ];
    }

    private static KohColor Bgr555ToRgb(ushort v)
    {
        int r5 = v         & 0x1f;
        int g5 = (v >> 5)  & 0x1f;
        int b5 = (v >> 10) & 0x1f;
        // 5-bit → 8-bit with the standard "replicate the top bits" trick
        // so 0x1f becomes 0xff exactly.
        byte r = (byte)((r5 << 3) | (r5 >> 2));
        byte g = (byte)((g5 << 3) | (g5 >> 2));
        byte b = (byte)((b5 << 3) | (b5 >> 2));
        return new KohColor(r, g, b);
    }
}
