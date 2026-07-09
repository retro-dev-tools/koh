namespace Koh.GameBoy;

/// <summary>The joypad: read the d-pad + Start as a bitmask, and test whether a direction is set.
/// Games never poke the JOYP register or shuffle bitmasks themselves.</summary>
public static class Joypad
{
    // Button bits in the mask Read() returns (active-high).
    private const byte RightBit = 0x01;
    private const byte LeftBit = 0x02;
    private const byte UpBit = 0x04;
    private const byte DownBit = 0x08;

    /// <summary>Read the d-pad + Start as an active-high bitmask:
    /// bit0 Right, bit1 Left, bit2 Up, bit3 Down, bit4 Start.</summary>
    public static byte Read()
    {
        Hardware.JOYP = 0x20; // select the d-pad (P14 low)
        byte dpadRaw = Hardware.JOYP;
        dpadRaw = Hardware.JOYP; // read twice to let the lines settle

        Hardware.JOYP = 0x10; // select the buttons (P15 low)
        byte buttonsRaw = Hardware.JOYP;
        buttonsRaw = Hardware.JOYP;

        Hardware.JOYP = 0x30; // deselect

        // Inputs are active-low; ~x on a byte is (255 - x).
        byte dpad = (byte)((byte)(255 - dpadRaw) & 0x0F);
        byte buttons = (byte)((byte)(255 - buttonsRaw) & 0x0F);
        byte start = ((buttons & 0x08) != 0) ? (byte)0x10 : (byte)0x00; // buttons bit3 = Start
        return (byte)(dpad | start);
    }

    /// <summary>Whether a direction's bit is set in a button mask (typically a rising-edge mask).</summary>
    public static bool Held(byte buttons, Direction direction)
    {
        if (direction == Direction.Right)
            return (buttons & RightBit) != 0;
        if (direction == Direction.Left)
            return (buttons & LeftBit) != 0;
        if (direction == Direction.Up)
            return (buttons & UpBit) != 0;
        return (buttons & DownBit) != 0; // Down
    }
}
