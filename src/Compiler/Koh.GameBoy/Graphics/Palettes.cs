using Koh.GameBoy;

namespace Koh.GameBoy.Graphics;

/// <summary>DMG shades and CGB colors.</summary>
public static class Palettes
{
    /// <summary>Background palette.</summary>
    /// <remarks>IME disabled after CGB writes.</remarks>
    public static void SetBg(byte slot, ushort c0, ushort c1, ushort c2, ushort c3, byte dmgShades)
    {
        if (Video.IsCgb)
        {
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

    /// <summary>Object palette.</summary>
    /// <remarks>IME disabled after CGB writes.</remarks>
    public static void SetObj(byte slot, ushort c0, ushort c1, ushort c2, ushort c3, byte dmgShades)
    {
        if (Video.IsCgb)
        {
            Cgb.SetObjectColor(slot, 0, c0);
            Cgb.SetObjectColor(slot, 1, c1);
            Cgb.SetObjectColor(slot, 2, c2);
            Cgb.SetObjectColor(slot, 3, c3);
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
}
