using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>
/// One palette API for both machines, with EXPLICIT DUAL AUTHORING (graphics-library design doc §8,
/// resolved decision 2 — supersedes the original auto-quantize sketch in §3): every call takes the
/// four CGB RGB555 colors AND the DMG shade byte, so a game controls the DMG look on purpose instead
/// of hoping a luminance heuristic picks something reasonable.
///
/// Dispatch is on <see cref="Video.IsCgb"/> (cached once by <see cref="Video.Init"/>, per the
/// architecture rule that Graphics never re-derives the KEY1 check) rather than calling
/// <see cref="Cgb.IsColor"/> again here — callers are expected to have called <c>Video.Init()</c>
/// first, exactly like every other module in this library.
///
/// Both branches are immediate-checked, not deferred: BG/OBJ palette RAM (BCPS/BCPD, OCPS/OCPD) is
/// inaccessible to the CPU during PPU mode 3, same as VRAM (Pan Docs), so the CGB path gates on
/// <see cref="Ppu.WaitForVramAccess"/> before writing. BGP/OBP0/OBP1 are plain always-accessible
/// cells, so the DMG path writes straight through.
/// </summary>
public static class Palettes
{
    /// <summary>Sets background palette <paramref name="slot"/> (0-7, CGB only — DMG has exactly one
    /// BG palette, so only slot 0 is DMG-visible; higher slots are a silent no-op on DMG). On CGB,
    /// writes all four RGB555 colors to palette RAM via <see cref="Cgb.SetBackgroundColor"/> (already
    /// gates writes correctly and no-ops on DMG itself, but the explicit <see cref="Video.IsCgb"/>
    /// branch here is what selects <paramref name="dmgShades"/> instead on DMG). On DMG (slot 0 only),
    /// writes <paramref name="dmgShades"/> to BGP.</summary>
    public static void SetBg(byte slot, ushort c0, ushort c1, ushort c2, ushort c3, byte dmgShades)
    {
        if (Video.IsCgb)
        {
            Ppu.WaitForVramAccess();
            Cgb.SetBackgroundColor(slot, 0, c0);
            Cgb.SetBackgroundColor(slot, 1, c1);
            Cgb.SetBackgroundColor(slot, 2, c2);
            Cgb.SetBackgroundColor(slot, 3, c3);
        }
        else if (slot == 0)
        {
            Hardware.BGP = dmgShades;
        }
    }

    /// <summary>Sets object palette <paramref name="slot"/> (0-7, CGB only; <paramref name="c0"/> is
    /// always transparent on both machines regardless of the value passed). On CGB, writes all four
    /// RGB555 colors to object palette RAM via OCPS/OCPD (mirrors <see cref="Cgb.SetBackgroundColor"/>'s
    /// index-and-write-twice protocol — Cgb.cs has no OBJ variant, hence the private helper below). On
    /// DMG, only slots 0 and 1 exist in hardware (OBP0/OBP1); slot 0 writes OBP0, slot 1 writes OBP1,
    /// and slots 2-7 are a silent no-op on DMG.</summary>
    public static void SetObj(byte slot, ushort c0, ushort c1, ushort c2, ushort c3, byte dmgShades)
    {
        if (Video.IsCgb)
        {
            Ppu.WaitForVramAccess();
            SetObjectColor(slot, 0, c0);
            SetObjectColor(slot, 1, c1);
            SetObjectColor(slot, 2, c2);
            SetObjectColor(slot, 3, c3);
        }
        else if (slot == 0)
        {
            Hardware.OBP0 = dmgShades;
        }
        else if (slot == 1)
        {
            Hardware.OBP1 = dmgShades;
        }
    }

    /// <summary>Same OCPS/OCPD index-and-write-twice protocol as <see cref="Cgb.SetBackgroundColor"/>,
    /// targeting object palette RAM instead of background palette RAM.</summary>
    private static void SetObjectColor(byte palette, byte color, ushort rgb555)
    {
        byte index = (byte)(((palette & 7) * 8 + (color & 3) * 2) & 0x3F);
        Hardware.OCPS = index;
        Hardware.OCPD = (byte)rgb555;
        Hardware.OCPS = (byte)(index + 1);
        Hardware.OCPD = (byte)(rgb555 >> 8);
    }
}
