namespace Koh.Emulator.Core.Joypad;

[Flags]
public enum JoypadButton : byte
{
    None   = 0,
    Right  = 1 << 0,
    Left   = 1 << 1,
    Up     = 1 << 2,
    Down   = 1 << 3,
    A      = 1 << 4,
    B      = 1 << 5,
    Select = 1 << 6,
    Start  = 1 << 7,
}

public struct JoypadState
{
    public JoypadButton Pressed;

    public readonly bool IsPressed(JoypadButton button) => (Pressed & button) != 0;

    public void Press(JoypadButton button) => Pressed |= button;
    public void Release(JoypadButton button) => Pressed &= ~button;
}
