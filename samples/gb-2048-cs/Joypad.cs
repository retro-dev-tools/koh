// Input hardware abstraction: read the joypad matrix and answer "is this direction newly pressed?"
// so the game never pokes the JOYP register or shuffles bitmasks itself.

static class Joypad
{
    // Read the d-pad + Start as an active-high bitmask:
    //   bit0 Right, bit1 Left, bit2 Up, bit3 Down, bit4 Start.
    internal static byte Read()
    {
        Hardware.JOYP = 0x20; // select the d-pad (P14 low)
        byte d = Hardware.JOYP;
        d = Hardware.JOYP; // read twice to let the lines settle

        Hardware.JOYP = 0x10; // select the buttons (P15 low)
        byte b = Hardware.JOYP;
        b = Hardware.JOYP;

        Hardware.JOYP = 0x30; // deselect

        // Inputs are active-low; ~x on a byte is (255 - x).
        byte dpad = (byte)((byte)(255 - d) & 0x0F);
        byte buttons = (byte)((byte)(255 - b) & 0x0F);
        byte start = ((buttons & 0x08) != 0) ? (byte)0x10 : (byte)0x00; // buttons bit3 = Start
        return (byte)(dpad | start);
    }

    // Whether a direction's bit is set in a button mask (typically a rising-edge mask).
    internal static bool Held(byte buttons, Direction dir)
    {
        if (dir == Direction.Right)
            return (buttons & 0x01) != 0;
        if (dir == Direction.Left)
            return (buttons & 0x02) != 0;
        if (dir == Direction.Up)
            return (buttons & 0x04) != 0;
        return (buttons & 0x08) != 0; // Down
    }
}
